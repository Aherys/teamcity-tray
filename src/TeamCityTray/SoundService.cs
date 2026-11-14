using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Text;

namespace TeamCityTray;

public sealed class SoundService
{
    private const int SampleRate = 22050;

    private readonly byte[] _success = Synthesize(new[] { (523.25, 130), (783.99, 260) });
    private readonly byte[] _failure = Synthesize(new[] { (220.0, 220), (164.81, 380) });
    private readonly Settings _settings;
    private SoundPlayer? _player;
    private MemoryStream? _stream;

    public SoundService(Settings settings) => _settings = settings;

    public void Play(bool failure)
    {
        if (_settings.Muted)
        {
            return;
        }
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _stream?.Dispose();

            var custom = failure ? _settings.FailureWavPath : _settings.SuccessWavPath;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            {
                _stream = null;
                _player = new SoundPlayer(custom);
            }
            else
            {
                _stream = new MemoryStream(failure ? _failure : _success);
                _player = new SoundPlayer(_stream);
            }
            _player.Play();
        }
        catch (Exception)
        {
            // A broken wav must not break polling.
        }
    }

    private static byte[] Synthesize((double Frequency, int Milliseconds)[] notes)
    {
        var samples = new List<short>();
        foreach (var (frequency, milliseconds) in notes)
        {
            var length = SampleRate * milliseconds / 1000;
            for (var i = 0; i < length; i++)
            {
                var t = i / (double)SampleRate;
                var attack = Math.Min(1.0, i / (SampleRate * 0.008));
                var envelope = attack * Math.Exp(-3.5 * i / length);
                var wave = (Math.Sin(2 * Math.PI * frequency * t) + 0.3 * Math.Sin(4 * Math.PI * frequency * t)) / 1.3;
                samples.Add((short)(wave * envelope * 0.45 * short.MaxValue));
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        var dataBytes = samples.Count * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            w.Write(s);
        }
        w.Flush();
        return ms.ToArray();
    }
}
