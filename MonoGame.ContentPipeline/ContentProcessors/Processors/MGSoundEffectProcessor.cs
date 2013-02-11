﻿//#define ADPCM

using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Collections.ObjectModel;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using Microsoft.Xna.Framework.Content.Pipeline.Audio;
using NAudio.Wave;

namespace MonoGameContentProcessors.Processors
{
    [ContentProcessor(DisplayName = "MonoGame SoundEffect")]
    public class MGSoundEffectProcessor : SoundEffectProcessor
    {
        public override SoundEffectContent Process(AudioContent input, ContentProcessorContext context)
        {
#if !ADPCM
            // Fallback if we aren't buiding for iOS.
            var platform = ContentHelper.GetMonoGamePlatform();
            if (platform != MonoGamePlatform.iOS)
                return base.Process(input, context);
#endif

            bool useAdpcm = false;
            var targetSampleRate = input.Format.SampleRate;

            // XNA SoundEffects have their sample rate changed based on the quality setting on the processor.
            //http://blogs.msdn.com/b/etayrien/archive/2008/09/22/audio-input-and-output-formats.aspx
            switch(this.Quality)
            {
                case ConversionQuality.Best:
                    return base.Process(input, context);

                case ConversionQuality.Medium:
                    useAdpcm = true;
                    break;

                case ConversionQuality.Low:
                    useAdpcm = true;
                    targetSampleRate = (int)(targetSampleRate * 0.5f);
                    break;
            }

            targetSampleRate = Math.Max(8000, targetSampleRate);

            var wavStream = new MemoryStream();
            WaveFormat outputFormat;
            try
            {
                outputFormat = useAdpcm
                                   ? AudioConverter.ToImaAdpcm(input.FileName, wavStream, targetSampleRate,
                                                            input.Format.ChannelCount)
                                   : AudioConverter.ConvertFile(input.FileName, wavStream, AudioFileType.Wav,
                                                                targetSampleRate,
                                                                input.Format.BitsPerSample,
                                                                input.Format.ChannelCount);
            }
            catch (Exception ex)
            {
                Quality = ConversionQuality.Best;
                return base.Process(input, context);
            }

            var outputData = new ReadOnlyCollection<byte>(wavStream.ToArray());
            wavStream.Close();

            var waveFormatHeader = writeWavHeader(outputFormat);

            // SoundEffectContent is a sealed class, construct it using reflection
            var type = typeof(SoundEffectContent);
            ConstructorInfo c = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { typeof(ReadOnlyCollection<byte>), typeof(ReadOnlyCollection<byte>), typeof(int), typeof(int), typeof(int) }, null);

            var outputSoundEffectContent = (SoundEffectContent)c.Invoke(new Object[] { waveFormatHeader, outputData, input.LoopStart, input.LoopLength, (int)input.Duration.TotalMilliseconds });
            return outputSoundEffectContent;
        }

        private ReadOnlyCollection<byte> writeWavHeader(WaveFormat header)
        {
            using (var writer = new BinaryWriter(new MemoryStream()))
            {
                bool isAdpcm = header is AdpcmWaveFormat || header is ImaAdpcmWaveFormat;

                writer.Write((short)(isAdpcm ? 2 : 1));
                writer.Write((short)header.Channels);
                writer.Write(header.SampleRate);
                writer.Write(header.AverageBytesPerSecond);
                writer.Write((short)header.BlockAlign);
                writer.Write((short)header.BitsPerSample);

                writer.BaseStream.Position = 0;
                var outputData = new byte[writer.BaseStream.Length];
                writer.BaseStream.Read(outputData, 0, outputData.Length);
                return new ReadOnlyCollection<byte>(outputData);
            }
        }

    }
}
