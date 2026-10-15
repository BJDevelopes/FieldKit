// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Capture;

/// <summary>Sample layout of a capture device buffer.</summary>
public enum AudioSampleKind { Float32, Pcm16, Pcm24, Pcm32 }

/// <summary>
/// Converts device audio (any rate, any channel count, float or integer) to 16-bit stereo PCM at
/// the encoder rate, with a stateful linear resampler so chunk boundaries stay seamless. Pure.
/// </summary>
public sealed class AudioConverter
{
    public const int OutputChannels = 2;
    private readonly int _inputRate;
    private readonly int _inputChannels;
    private readonly AudioSampleKind _kind;
    private readonly double _step;
    private double _position;
    private float _lastLeft;
    private float _lastRight;
    private bool _hasLast;

    public AudioConverter(int inputRate, int inputChannels, AudioSampleKind kind, int outputRate = 48000)
    {
        if (inputRate <= 0 || inputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        _inputRate = inputRate;
        _inputChannels = inputChannels;
        _kind = kind;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
    }

    public int OutputRate { get; }

    public int BytesPerInputFrame => _inputChannels * _kind switch
    {
        AudioSampleKind.Pcm16 => 2,
        AudioSampleKind.Pcm24 => 3,
        _ => 4,
    };

    /// <summary>Converts one device buffer; returns interleaved 16-bit stereo bytes.</summary>
    public byte[] Convert(ReadOnlySpan<byte> input)
    {
        var frames = input.Length / BytesPerInputFrame;
        if (frames == 0) return [];
        // Downmix to stereo floats first.
        var left = new float[frames];
        var right = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var offset = f * BytesPerInputFrame;
            var l = Read(input, offset, 0);
            var r = _inputChannels > 1 ? Read(input, offset, 1) : l;
            left[f] = l;
            right[f] = r;
        }

        if (_inputRate == OutputRate)
        {
            var direct = new byte[frames * 4];
            for (var f = 0; f < frames; f++) Write(direct, f, left[f], right[f]);
            _lastLeft = left[^1];
            _lastRight = right[^1];
            _hasLast = true;
            return direct;
        }

        // Linear interpolation; position is measured from the previous chunk's last frame (index -1).
        var output = new List<byte>((int)(frames / _step * 4) + 8);
        var buffer = new byte[4];
        while (true)
        {
            var index = (int)Math.Floor(_position);
            if (index + 1 >= frames) break;
            var frac = (float)(_position - index);
            var l0 = index < 0 ? (_hasLast ? _lastLeft : left[0]) : left[index];
            var r0 = index < 0 ? (_hasLast ? _lastRight : right[0]) : right[index];
            var l1 = left[index + 1];
            var r1 = right[index + 1];
            Write(buffer, 0, l0 + (l1 - l0) * frac, r0 + (r1 - r0) * frac);
            output.AddRange(buffer);
            _position += _step;
        }
        // Continue from the last frame of this chunk next time.
        _position -= frames;
        _lastLeft = left[^1];
        _lastRight = right[^1];
        _hasLast = true;
        return output.ToArray();
    }

    private float Read(ReadOnlySpan<byte> input, int frameOffset, int channel)
    {
        switch (_kind)
        {
            case AudioSampleKind.Pcm16:
                return BitConverter.ToInt16(input.Slice(frameOffset + channel * 2, 2)) / 32768f;
            case AudioSampleKind.Pcm24:
            {
                var o = frameOffset + channel * 3;
                var v = input[o] | (input[o + 1] << 8) | ((sbyte)input[o + 2] << 16);
                return v / 8388608f;
            }
            case AudioSampleKind.Pcm32:
                return BitConverter.ToInt32(input.Slice(frameOffset + channel * 4, 4)) / 2147483648f;
            default:
                return BitConverter.ToSingle(input.Slice(frameOffset + channel * 4, 4));
        }
    }

    private static void Write(byte[] output, int frame, float left, float right)
    {
        var l = (short)Math.Clamp((int)MathF.Round(left * 32767f), short.MinValue, short.MaxValue);
        var r = (short)Math.Clamp((int)MathF.Round(right * 32767f), short.MinValue, short.MaxValue);
        output[frame * 4] = (byte)l;
        output[frame * 4 + 1] = (byte)(l >> 8);
        output[frame * 4 + 2] = (byte)r;
        output[frame * 4 + 3] = (byte)(r >> 8);
    }
}
