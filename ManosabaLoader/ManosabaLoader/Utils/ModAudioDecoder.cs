using System;
using System.IO;

using NVorbis;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// 纯 managed 音频解码工具，支持 WAV（任意采样率/位深度）和 OGG Vorbis。
    /// 不依赖 IL2CPP，所有解码在 managed 侧完成。
    /// </summary>
    public static class ModAudioDecoder
    {
        public enum AudioFormat { Unknown, Wav, Ogg }

        public readonly struct AudioData
        {
            public readonly float[] Samples;
            public readonly int Channels;
            public readonly int SampleRate;
            public readonly int SampleCount;

            public AudioData(float[] samples, int channels, int sampleRate, int sampleCount)
            {
                Samples = samples;
                Channels = channels;
                SampleRate = sampleRate;
                SampleCount = sampleCount;
            }
        }

        /// <summary>通过魔数检测音频格式。</summary>
        public static AudioFormat DetectFormat(byte[] data)
        {
            if (data == null || data.Length < 4) return AudioFormat.Unknown;

            // RIFF header → WAV
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
                return AudioFormat.Wav;

            // OggS header → OGG Vorbis
            if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
                return AudioFormat.Ogg;

            return AudioFormat.Unknown;
        }

        /// <summary>自动检测格式并解码。</summary>
        public static AudioData Decode(byte[] data)
        {
            var format = DetectFormat(data);
            return format switch
            {
                AudioFormat.Wav => DecodeWav(data),
                AudioFormat.Ogg => DecodeOgg(data),
                _ => throw new NotSupportedException($"Unsupported audio format (magic: {(data.Length >= 4 ? $"0x{data[0]:X2}{data[1]:X2}{data[2]:X2}{data[3]:X2}" : "too short")})")
            };
        }

        /// <summary>
        /// 解码 WAV 文件，支持任意采样率和多种位深度。
        /// 通过 RIFF chunk 扫描定位 fmt 和 data 块（不使用固定偏移量）。
        /// </summary>
        public static AudioData DecodeWav(byte[] data)
        {
            if (data.Length < 12)
                throw new InvalidDataException("WAV data too short for RIFF header.");

            // Validate RIFF header
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
                throw new InvalidDataException("Not a valid RIFF file.");
            if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
                throw new InvalidDataException("Not a valid WAVE file.");

            // Parse chunks
            int audioFormat = 0;
            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            bool fmtFound = false;

            byte[] rawData = null;

            int pos = 12; // skip RIFF header
            while (pos + 8 <= data.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                int chunkSize = BitConverter.ToInt32(data, pos + 4);
                int chunkDataStart = pos + 8;

                if (chunkId == "fmt ")
                {
                    if (chunkSize < 16)
                        throw new InvalidDataException("fmt chunk too small.");
                    audioFormat = BitConverter.ToInt16(data, chunkDataStart);
                    channels = BitConverter.ToInt16(data, chunkDataStart + 2);
                    sampleRate = BitConverter.ToInt32(data, chunkDataStart + 4);
                    // skip byteRate (4) and blockAlign (2)
                    bitsPerSample = BitConverter.ToInt16(data, chunkDataStart + 14);
                    fmtFound = true;
                }
                else if (chunkId == "data")
                {
                    int dataLength = Math.Min(chunkSize, data.Length - chunkDataStart);
                    rawData = new byte[dataLength];
                    Buffer.BlockCopy(data, chunkDataStart, rawData, 0, dataLength);
                }

                // Advance to next chunk (chunks are word-aligned)
                pos = chunkDataStart + chunkSize;
                if (chunkSize % 2 != 0) pos++;
            }

            if (!fmtFound)
                throw new InvalidDataException("WAV file missing fmt chunk.");
            if (rawData == null || rawData.Length == 0)
                throw new InvalidDataException("WAV file missing data chunk.");

            // audioFormat: 1 = PCM integer, 3 = IEEE float
            if (audioFormat != 1 && audioFormat != 3)
                throw new NotSupportedException($"Unsupported WAV audio format: {audioFormat} (only PCM and IEEE float supported).");

            float[] samples = ConvertToFloat(rawData, audioFormat, bitsPerSample);
            int sampleCount = samples.Length / channels;

            return new AudioData(samples, channels, sampleRate, sampleCount);
        }

        /// <summary>
        /// 解码 OGG Vorbis 文件（通过 NVorbis）。
        /// </summary>
        public static AudioData DecodeOgg(byte[] data)
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new VorbisReader(stream);

            int channels = reader.Channels;
            int sampleRate = reader.SampleRate;
            long totalSamples = reader.TotalSamples;

            // totalSamples 是每声道的采样数，总 float 数 = totalSamples * channels
            int totalFloats = (int)(totalSamples * channels);
            float[] samples = new float[totalFloats];

            int offset = 0;
            int remaining = totalFloats;
            while (remaining > 0)
            {
                int read = reader.ReadSamples(samples, offset, remaining);
                if (read <= 0) break;
                offset += read;
                remaining -= read;
            }

            // 如果实际读取量小于预期，截断
            if (offset < totalFloats)
            {
                Array.Resize(ref samples, offset);
                totalSamples = offset / channels;
            }

            return new AudioData(samples, channels, sampleRate, (int)totalSamples);
        }

        /// <summary>
        /// 将原始 PCM/Float 字节数据转换为 Unity 所需的 float[] 格式（-1.0 ~ 1.0）。
        /// </summary>
        private static float[] ConvertToFloat(byte[] rawData, int audioFormat, int bitsPerSample)
        {
            if (audioFormat == 3 && bitsPerSample == 32)
            {
                // IEEE 32-bit float
                int count = rawData.Length / 4;
                float[] samples = new float[count];
                Buffer.BlockCopy(rawData, 0, samples, 0, rawData.Length);
                return samples;
            }

            if (audioFormat == 1)
            {
                return bitsPerSample switch
                {
                    8 => ConvertPcm8(rawData),
                    16 => ConvertPcm16(rawData),
                    24 => ConvertPcm24(rawData),
                    32 => ConvertPcm32(rawData),
                    _ => throw new NotSupportedException($"Unsupported PCM bit depth: {bitsPerSample}")
                };
            }

            throw new NotSupportedException($"Unsupported audio format {audioFormat} with {bitsPerSample} bits.");
        }

        private static float[] ConvertPcm8(byte[] rawData)
        {
            float[] samples = new float[rawData.Length];
            for (int i = 0; i < rawData.Length; i++)
            {
                // 8-bit PCM is unsigned (0-255), center at 128
                samples[i] = (rawData[i] - 128) / 128f;
            }
            return samples;
        }

        private static float[] ConvertPcm16(byte[] rawData)
        {
            int count = rawData.Length / 2;
            float[] samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                short value = BitConverter.ToInt16(rawData, i * 2);
                samples[i] = value / 32768f;
            }
            return samples;
        }

        private static float[] ConvertPcm24(byte[] rawData)
        {
            int count = rawData.Length / 3;
            float[] samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                int offset = i * 3;
                // 24-bit signed, little-endian: sign-extend to 32-bit
                int value = rawData[offset] | (rawData[offset + 1] << 8) | (rawData[offset + 2] << 16);
                if ((value & 0x800000) != 0)
                    value |= unchecked((int)0xFF000000); // sign extend
                samples[i] = value / 8388608f; // 2^23
            }
            return samples;
        }

        private static float[] ConvertPcm32(byte[] rawData)
        {
            int count = rawData.Length / 4;
            float[] samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                int value = BitConverter.ToInt32(rawData, i * 4);
                samples[i] = value / 2147483648f; // 2^31
            }
            return samples;
        }
    }
}
