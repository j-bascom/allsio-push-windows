using NAudio.Wave;
using AllsioPush.Config;
using AllsioPush.Models;

namespace AllsioPush.Services;

public class ToneService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private List<PlatformTone> _toneCache = new();
    private DateTime _cacheLoadedAt = DateTime.MinValue;
    private const int CacheMaxAgeMinutes = 60;

    public ToneService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task LoadTonesAsync()
    {
        try
        {
            var baseUrl = _settings.AdminBase;
            var url = $"{baseUrl}/api/platform-tones";
            var response = await _http.GetStringAsync(url);
            var data = System.Text.Json.JsonSerializer.Deserialize<PlatformTonesResponse>(
                response,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _toneCache = data?.Tones ?? new();
            _cacheLoadedAt = DateTime.Now;
            System.Diagnostics.Debug.WriteLine($"[Tones] Loaded {_toneCache.Count} platform tones from server");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Tones] Failed to load tones: {ex.Message}");
        }
    }

    public async Task RefreshIfStaleAsync()
    {
        if ((DateTime.Now - _cacheLoadedAt).TotalMinutes > CacheMaxAgeMinutes)
            await LoadTonesAsync();
    }

    public void PlayNotificationSound(string? soundName)
    {
        if (!_settings.SoundEnabled) return;
        if (soundName == "none" || soundName == "false") return;

        var slug = soundName ?? "chime";

        var platformTone = _toneCache.FirstOrDefault(t => t.Slug == slug);
        if (platformTone != null)
        {
            PlayToneSpec(platformTone.SoundSpec.Steps);
            return;
        }

        PlayBuiltInPreset(slug);
    }

    private void PlayToneSpec(List<ToneStep> steps)
    {
        if (steps.Count == 0) return;
        var safeSteps = steps.Take(32).ToList();

        Task.Run(() =>
        {
            try
            {
                var sampleRate = 44100;
                var totalDuration = safeSteps.Max(s => s.Start + s.Dur);
                var totalSamples = (int)((totalDuration + 0.1) * sampleRate);
                var buffer = new float[totalSamples];

                foreach (var step in safeSteps)
                {
                    var freq = Math.Clamp(step.Freq, 20, 20000);
                    var gain = Math.Clamp(step.Gain, 0, 1);
                    var dur = Math.Clamp(step.Dur, 0, 5);
                    var start = Math.Max(step.Start, 0);

                    var startSample = (int)(start * sampleRate);
                    var durSamples = (int)(dur * sampleRate);

                    for (int i = 0; i < durSamples; i++)
                    {
                        var sampleIndex = startSample + i;
                        if (sampleIndex >= buffer.Length) break;

                        var t = (double)i / sampleRate;
                        var phase = 2 * Math.PI * freq * t;

                        float sample = step.Type switch
                        {
                            "square"   => Math.Sin(phase) >= 0 ? 1f : -1f,
                            "sawtooth" => (float)(2 * (t * freq - Math.Floor(t * freq + 0.5))),
                            "triangle" => (float)(2 * Math.Abs(2 * (t * freq - Math.Floor(t * freq + 0.5))) - 1),
                            _          => (float)Math.Sin(phase)
                        };

                        var envelope = 1.0f;
                        var fadeStart = durSamples * 0.9;
                        if (i > fadeStart)
                            envelope = (float)(1.0 - (i - fadeStart) / (durSamples - fadeStart));

                        buffer[sampleIndex] += sample * (float)gain * envelope;
                    }
                }

                var maxVal = buffer.Max(Math.Abs);
                if (maxVal > 1.0f)
                    for (int i = 0; i < buffer.Length; i++)
                        buffer[i] /= maxVal;

                PlayBuffer(buffer, sampleRate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Tones] PlayToneSpec error: {ex.Message}");
            }
        });
    }

    private void PlayBuiltInPreset(string preset)
    {
        Task.Run(() =>
        {
            try
            {
                var sampleRate = 44100;
                var buffer = preset switch
                {
                    "alert"    => GenerateAlert(sampleRate),
                    "soft"     => GenerateSoft(sampleRate),
                    "escalate" => GenerateEscalate(sampleRate),
                    _          => GenerateChime(sampleRate)
                };
                PlayBuffer(buffer, sampleRate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Tones] Preset error: {ex.Message}");
            }
        });
    }

    private static void PlayBuffer(float[] buffer, int sampleRate)
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        var bytes = new byte[buffer.Length * 4];
        System.Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
        var provider = new RawSourceWaveStream(new MemoryStream(bytes), waveFormat);
        using var waveOut = new WaveOutEvent();
        waveOut.Init(provider);
        waveOut.Play();
        while (waveOut.PlaybackState == PlaybackState.Playing)
            Thread.Sleep(50);
    }

    private static float[] GenerateChime(int sampleRate) =>
        SynthesizeSteps(new[]
        {
            new ToneStep { Freq=880,  Start=0,    Dur=0.15, Type="sine", Gain=0.3 },
            new ToneStep { Freq=1100, Start=0.15, Dur=0.15, Type="sine", Gain=0.3 }
        }, sampleRate);

    private static float[] GenerateAlert(int sampleRate) =>
        SynthesizeSteps(new[]
        {
            new ToneStep { Freq=1000, Start=0,    Dur=0.08, Type="sine", Gain=0.35 },
            new ToneStep { Freq=1000, Start=0.12, Dur=0.08, Type="sine", Gain=0.35 },
            new ToneStep { Freq=1000, Start=0.24, Dur=0.08, Type="sine", Gain=0.35 }
        }, sampleRate);

    private static float[] GenerateSoft(int sampleRate) =>
        SynthesizeSteps(new[]
        {
            new ToneStep { Freq=440, Start=0, Dur=0.5, Type="sine", Gain=0.2 }
        }, sampleRate);

    private static float[] GenerateEscalate(int sampleRate) =>
        SynthesizeSteps(new[]
        {
            new ToneStep { Freq=600,  Start=0,    Dur=0.12, Type="sine", Gain=0.3 },
            new ToneStep { Freq=800,  Start=0.15, Dur=0.12, Type="sine", Gain=0.3 },
            new ToneStep { Freq=1000, Start=0.30, Dur=0.12, Type="sine", Gain=0.3 }
        }, sampleRate);

    private static float[] SynthesizeSteps(ToneStep[] steps, int sampleRate)
    {
        var totalDur = steps.Max(s => s.Start + s.Dur) + 0.1;
        var buffer = new float[(int)(totalDur * sampleRate)];

        foreach (var step in steps)
        {
            var startSample = (int)(step.Start * sampleRate);
            var durSamples = (int)(step.Dur * sampleRate);

            for (int i = 0; i < durSamples; i++)
            {
                if (startSample + i >= buffer.Length) break;
                var t = (double)i / sampleRate;
                var val = (float)(Math.Sin(2 * Math.PI * step.Freq * t) * step.Gain);
                var fade = i > durSamples * 0.9
                    ? (float)(1.0 - (i - durSamples * 0.9) / (durSamples * 0.1))
                    : 1f;
                buffer[startSample + i] += val * fade;
            }
        }
        return buffer;
    }

    private class PlatformTonesResponse
    {
        public List<PlatformTone> Tones { get; set; } = new();
    }

    public void Dispose() => _http.Dispose();
}
