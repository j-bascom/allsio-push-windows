namespace AllsioPush.Models;

public class PlatformTone
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ToneSoundSpec SoundSpec { get; set; } = new();
}

public class ToneSoundSpec
{
    public List<ToneStep> Steps { get; set; } = new();
}

public class ToneStep
{
    public double Freq { get; set; } = 440;
    public double Start { get; set; } = 0;
    public double Dur { get; set; } = 0.1;
    public string Type { get; set; } = "sine";
    public double Gain { get; set; } = 0.3;
}
