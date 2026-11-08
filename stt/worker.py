"""
XScribe Desktop — STT worker.

Captures your microphone ("you") and the system's default audio-output
loopback ("them" — i.e. the other side of the call), transcribes each with a
local faster-whisper model, and prints one JSON line per finished sentence to
stdout for the WPF app to read:

    {"speaker": "you" | "them", "text": "...", "ts": 1730000000000}

Each speaker gets its own model instance and its own thread end-to-end
(capture -> endpoint -> transcribe -> print), so a busy "them" stream never
delays "you" transcriptions (and vice versa).

Runs until killed (Ctrl+C or parent process termination).
"""
import argparse
import json
import os
import sys
import threading
import time
import wave

import numpy as np
import pyaudiowpatch as pyaudio
from faster_whisper import WhisperModel
from scipy.signal import resample_poly

WHISPER_RATE = 16000
FRAME_MS = 100  # audio read granularity
DEBUG_RMS = False
DEBUG_WAV_DIR = None
PRINT_LOCK = threading.Lock()


def save_debug_wav(speaker, chunk, rate):
    os.makedirs(DEBUG_WAV_DIR, exist_ok=True)
    path = os.path.join(DEBUG_WAV_DIR, f"{speaker}-{int(time.time() * 1000)}.wav")
    pcm16 = (np.clip(chunk, -1.0, 1.0) * 32767).astype(np.int16)
    with wave.open(path, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(rate)
        wf.writeframes(pcm16.tobytes())
    log(f"{speaker}: saved debug wav -> {path}")


def log(msg):
    print(f"[worker] {msg}", file=sys.stderr, flush=True)


class SpeakerCapture:
    """Captures one audio source, does simple energy-based endpointing, and
    transcribes each finished utterance with its own dedicated model instance."""

    def __init__(self, speaker, pa, model, device_index, channels, rate, is_loopback,
                 silence_ms, max_chunk_s, min_speech_s, rms_threshold):
        self.speaker = speaker
        self.pa = pa
        self.model = model
        self.device_index = device_index
        self.channels = channels
        self.rate = rate
        self.is_loopback = is_loopback
        self.silence_frames_needed = max(1, silence_ms // FRAME_MS)
        self.max_chunk_frames = max(1, int(max_chunk_s * 1000 // FRAME_MS))
        self.min_speech_frames = max(1, int(min_speech_s * 1000 // FRAME_MS))
        self.rms_threshold = rms_threshold
        self._stop = threading.Event()

    def stop(self):
        self._stop.set()

    def _to_mono_float(self, data):
        audio = np.frombuffer(data, dtype=np.int16).astype(np.float32) / 32768.0
        if self.channels > 1:
            audio = audio.reshape(-1, self.channels).mean(axis=1)
        return audio

    def _transcribe(self, chunk):
        if DEBUG_WAV_DIR:
            save_debug_wav(self.speaker, chunk, self.rate)

        if self.rate != WHISPER_RATE:
            chunk = resample_poly(chunk, WHISPER_RATE, self.rate).astype(np.float32)

        segments, _ = self.model.transcribe(
            chunk,
            language="en",
            vad_filter=True,
            beam_size=2,
            condition_on_previous_text=False,
            no_speech_threshold=0.6,
        )
        text = "".join(seg.text for seg in segments).strip()
        if text:
            with PRINT_LOCK:
                print(json.dumps({
                    "speaker": self.speaker,
                    "text": text,
                    "ts": int(time.time() * 1000),
                }), flush=True)

    def run(self):
        frame_size = int(self.rate * FRAME_MS / 1000)
        stream = self.pa.open(
            format=pyaudio.paInt16,
            channels=self.channels,
            rate=self.rate,
            input=True,
            input_device_index=self.device_index,
            frames_per_buffer=frame_size,
        )
        log(f"{self.speaker}: listening on device #{self.device_index} "
            f"({self.rate} Hz, {self.channels} ch, loopback={self.is_loopback})")

        MAX_TRAILING_PAD_FRAMES = 3  # ~300ms of trailing silence sent to whisper, not the full gap

        buffer = []
        speech_frames = 0
        silence_run = 0
        trailing_pad = 0
        peak_rms = 0.0
        last_report = time.time()

        try:
            while not self._stop.is_set():
                try:
                    data = stream.read(frame_size, exception_on_overflow=False)
                except OSError as e:
                    log(f"{self.speaker}: read error {e}")
                    continue

                mono = self._to_mono_float(data)
                rms = float(np.sqrt(np.mean(mono ** 2))) if mono.size else 0.0
                peak_rms = max(peak_rms, rms)
                if DEBUG_RMS and time.time() - last_report > 1.0:
                    log(f"{self.speaker}: rms peak(last 1s)={peak_rms:.4f} threshold={self.rms_threshold}")
                    peak_rms = 0.0
                    last_report = time.time()
                is_speech = rms > self.rms_threshold

                if is_speech:
                    buffer.append(mono)
                    speech_frames += 1
                    silence_run = 0
                    trailing_pad = 0
                elif buffer:
                    silence_run += 1
                    if trailing_pad < MAX_TRAILING_PAD_FRAMES:
                        buffer.append(mono)  # small trailing pad only — long silence hurts whisper
                        trailing_pad += 1

                hit_silence_end = buffer and silence_run >= self.silence_frames_needed
                hit_max_len = len(buffer) >= self.max_chunk_frames

                if buffer and (hit_silence_end or hit_max_len):
                    if speech_frames >= self.min_speech_frames:
                        chunk = np.concatenate(buffer)
                        try:
                            self._transcribe(chunk)
                        except Exception as e:
                            log(f"{self.speaker}: transcribe error {e}")
                    buffer = []
                    speech_frames = 0
                    silence_run = 0
                    trailing_pad = 0
        finally:
            stream.stop_stream()
            stream.close()


def list_devices(pa):
    wasapi = pa.get_host_api_info_by_type(pyaudio.paWASAPI)
    mics = []
    for i in range(pa.get_device_count()):
        info = pa.get_device_info_by_index(i)
        if info.get("hostApi") == wasapi["index"] and info.get("maxInputChannels", 0) > 0 \
                and not info.get("isLoopbackDevice", False):
            mics.append({"index": info["index"], "name": info["name"]})

    speakers = [{"index": d["index"], "name": d["name"]}
                for d in pa.get_loopback_device_info_generator()]

    print(json.dumps({"mics": mics, "speakers": speakers}), flush=True)


def loopback_for_output_index(pa, output_index):
    """Given a render (output) device index, find its matching WASAPI loopback device."""
    target = pa.get_device_info_by_index(output_index)
    if target.get("isLoopbackDevice", False):
        return target
    for loopback in pa.get_loopback_device_info_generator():
        if target["name"] in loopback["name"]:
            return loopback
    raise RuntimeError(f"No loopback device found for output device '{target['name']}'")


def pick_devices(pa, mic_index=None, speaker_index=None):
    wasapi = pa.get_host_api_info_by_type(pyaudio.paWASAPI)

    mic = pa.get_device_info_by_index(mic_index) if mic_index is not None \
        else pa.get_device_info_by_index(wasapi["defaultInputDevice"])

    if speaker_index is not None:
        speakers = loopback_for_output_index(pa, speaker_index)
    else:
        speakers = pa.get_device_info_by_index(wasapi["defaultOutputDevice"])
        if not speakers.get("isLoopbackDevice", False):
            speakers = loopback_for_output_index(pa, speakers["index"])

    return mic, speakers


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="small.en")
    ap.add_argument("--device", default="cpu", choices=["cpu", "cuda"])
    ap.add_argument("--compute-type", default="int8")
    ap.add_argument("--silence-ms", type=int, default=600)
    ap.add_argument("--max-chunk-s", type=float, default=12.0)
    ap.add_argument("--min-speech-s", type=float, default=0.4)
    ap.add_argument("--rms-threshold", type=float, default=0.01)
    ap.add_argument("--mic-only", action="store_true")
    ap.add_argument("--speaker-only", action="store_true")
    ap.add_argument("--mic-index", type=int, default=None)
    ap.add_argument("--speaker-index", type=int, default=None)
    ap.add_argument("--list-devices", action="store_true")
    ap.add_argument("--debug-rms", action="store_true")
    ap.add_argument("--debug-wav-dir", default=None)
    ap.add_argument("--cpu-threads", type=int, default=4)
    args = ap.parse_args()

    global DEBUG_RMS, DEBUG_WAV_DIR
    DEBUG_RMS = args.debug_rms
    DEBUG_WAV_DIR = args.debug_wav_dir

    if args.list_devices:
        pa = pyaudio.PyAudio()
        list_devices(pa)
        pa.terminate()
        return

    want_you = not args.speaker_only
    want_them = not args.mic_only

    log(f"loading whisper model '{args.model}' ({args.device}/{args.compute_type}, cpu_threads={args.cpu_threads})...")
    you_model = WhisperModel(args.model, device=args.device, compute_type=args.compute_type,
                              cpu_threads=args.cpu_threads) if want_you else None
    # Reuse the "you" model instance only when both streams share this single process
    # (standalone/legacy mode). When launched as one-stream-per-process (the normal
    # path from the WPF app), each process loads exactly the one model it needs —
    # that's what actually buys real parallelism, since two Python threads sharing
    # one process still fight over the GIL for everything outside the C++ inference call.
    # cpu_threads is capped per instance so two concurrent processes don't oversubscribe the CPU.
    them_model = you_model if (want_you and want_them) else \
        (WhisperModel(args.model, device=args.device, compute_type=args.compute_type,
                       cpu_threads=args.cpu_threads) if want_them else None)
    log("model ready")

    pa = pyaudio.PyAudio()
    mic, speakers = pick_devices(pa, args.mic_index, args.speaker_index)

    captures = []
    if want_you:
        captures.append(SpeakerCapture(
            "you", pa, you_model, mic["index"], mic["maxInputChannels"], int(mic["defaultSampleRate"]), False,
            args.silence_ms, args.max_chunk_s, args.min_speech_s, args.rms_threshold,
        ))
    if want_them:
        captures.append(SpeakerCapture(
            "them", pa, them_model, speakers["index"], speakers["maxInputChannels"],
            int(speakers["defaultSampleRate"]), True,
            args.silence_ms, args.max_chunk_s, args.min_speech_s, args.rms_threshold,
        ))

    threads = [threading.Thread(target=c.run, daemon=True) for c in captures]
    for t in threads:
        t.start()

    try:
        while True:
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    finally:
        for c in captures:
            c.stop()
        pa.terminate()


if __name__ == "__main__":
    main()
