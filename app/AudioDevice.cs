namespace XScribeDesktop;

public class AudioDevice
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
}

public class DeviceList
{
    public List<AudioDevice> Mics { get; set; } = new();
    public List<AudioDevice> Speakers { get; set; } = new();
}
