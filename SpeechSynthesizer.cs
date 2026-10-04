using System.Speech.Synthesis;
using Jarvis.Core;
using NAudio.Wave;

namespace Jarvis.Voice;

/// <summary>Интерфейс TTS: чтобы заменить голос (облачный, Piper и т.д.), реализуйте ITextToSpeech и поменяйте регистрацию в App.xaml.cs.</summary>
public interface ITextToSpeech
{
    Task SpeakAsync(string text);
}

public sealed class JarvisSpeechSynthesizer : ITextToSpeech
{
    private const float Pitch = 0.9f;   // 1.0 = без изменений; меньше — ниже и медленнее
    private readonly AppSettings _s;
    private readonly object _lock = new();

    public JarvisSpeechSynthesizer(AppSettings settings) => _s = settings;

    public static List<string> ListVoices()
    {
        try
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            return synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch { return new List<string>(); }
    }

    public static List<(int Index, string Name)> ListOutputDevices()
    {
        var list = new List<(int, string)>();
        for (var i = 0; i < WaveOut.DeviceCount; i++) list.Add((i, WaveOut.GetCapabilities(i).ProductName));
        return list;
    }

    public Task SpeakAsync(string text) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            SelectVoice(synth);
            synth.Rate = Math.Clamp(_s.SpeechRate + 1, -10, 10);   // +1 компенсирует замедление от понижения тона
            using var ms = new MemoryStream();
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);

            try
            {
                ms.Position = 0;
                using var reader = new WaveFileReader(ms);
                Play(new SampleTo16Bit(new JarvisVoiceEffect(reader.ToSampleProvider(), Pitch)));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "voice effect, fallback to plain voice");
                ms.Position = 0;
                using var plain = new WaveFileReader(ms);
                Play(plain);
            }
        }
    });

    private void Play(IWaveProvider provider)
    {
        using var output = new WaveOutEvent { DeviceNumber = _s.OutputDevice, Volume = Math.Clamp(_s.Volume, 0, 100) / 100f };
        using var done = new ManualResetEventSlim();
        output.PlaybackStopped += (_, _) => done.Set();
        output.Init(provider);
        output.Play();
        done.Wait();
    }

    private void SelectVoice(System.Speech.Synthesis.SpeechSynthesizer synth)
    {
        try
        {
            var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            var chosen = voices.FirstOrDefault(v => v.Name == _s.VoiceName)
                         ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "ru" && v.Gender == VoiceGender.Male)
                         ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "ru");
            if (chosen != null) synth.SelectVoice(chosen.Name);
        }
        catch (Exception ex) { Log.Error(ex, "select voice"); }
    }
}

/// <summary>Эффект «ИИ-голоса»: ниже тон, плотный низ, металлический призвук, короткое эхо и хвост.</summary>
internal sealed class JarvisVoiceEffect : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly float[] _comb;
    private readonly float[] _echo;
    private int _ci, _ei, _tail;
    private float _lp;

    public WaveFormat WaveFormat { get; }

    public JarvisVoiceEffect(ISampleProvider src, float pitch)
    {
        _src = src;
        var ch = src.WaveFormat.Channels;
        var rate = (int)(src.WaveFormat.SampleRate * pitch);   // «заниженная» частота = ниже тон
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, ch);
        _comb = new float[Math.Max(1, rate * ch / 85)];        // ~12 мс
        _echo = new float[Math.Max(1, rate * ch / 18)];        // ~55 мс
        _tail = rate * ch / 3;                                 // ~0,3 с хвоста
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = _src.Read(buffer, offset, count);
        if (n < count && _tail > 0)
        {
            var extra = Math.Min(count - n, _tail);
            Array.Clear(buffer, offset + n, extra);
            _tail -= extra;
            n += extra;
        }
        for (var i = 0; i < n; i++)
        {
            var x = buffer[offset + i];
            _lp += 0.08f * (x - _lp);
            x += 0.6f * _lp;                                   // плотный низ
            var c = x + 0.32f * _comb[_ci];                    // металлический «звон»
            _comb[_ci] = c;
            if (++_ci >= _comb.Length) _ci = 0;
            var e = c + 0.22f * _echo[_ei];                    // короткое эхо
            _echo[_ei] = c;
            if (++_ei >= _echo.Length) _ei = 0;
            buffer[offset + i] = MathF.Tanh(e * 1.1f) * 0.9f;  // мягкое ограничение
        }
        return n;
    }
}

/// <summary>Преобразование float-сэмплов в 16-битный PCM (собственная реализация, чтобы не зависеть от пространств имён NAudio).</summary>
internal sealed class SampleTo16Bit : IWaveProvider
{
    private readonly ISampleProvider _src;
    private float[] _buf = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public SampleTo16Bit(ISampleProvider src)
    {
        _src = src;
        WaveFormat = new WaveFormat(src.WaveFormat.SampleRate, 16, src.WaveFormat.Channels);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var samples = count / 2;
        if (_buf.Length < samples) _buf = new float[samples];
        var read = _src.Read(_buf, 0, samples);
        for (var i = 0; i < read; i++)
        {
            var v = (short)Math.Clamp(_buf[i] * 32767f, -32768f, 32767f);
            buffer[offset + i * 2] = (byte)(v & 0xFF);
            buffer[offset + i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return read * 2;
    }
}
