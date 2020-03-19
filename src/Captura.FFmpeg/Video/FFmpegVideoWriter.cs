﻿using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Captura.FFmpeg;

namespace Captura.Models
{
    /// <summary>
    /// Encode Video using FFmpeg.exe
    /// </summary>
    class FFmpegWriter : IVideoFileWriter
    {
        readonly NamedPipeServerStream _audioPipe;

        readonly Process _ffmpegProcess;
        readonly NamedPipeServerStream _ffmpegIn;
        byte[] _videoBuffer;

        // These semaphores prevent FFmpeg audio/video pipes getting deadlocked.
        readonly SemaphoreSlim _spAudio = new SemaphoreSlim(2),
            _spVideo = new SemaphoreSlim(2);

        // Timeout used with Semaphores, if elapsed would mean FFmpeg might be deadlocked.
        readonly TimeSpan _spTimeout = TimeSpan.FromSeconds(5);

        static string GetPipeName() => $"captura-{Guid.NewGuid()}";

        /// <summary>
        /// Creates a new instance of <see cref="FFmpegWriter"/>.
        /// </summary>
        public FFmpegWriter(FFmpegVideoWriterArgs Args)
        {
            if (!FFmpegService.FFmpegExists)
            {
                throw new FFmpegNotFoundException();
            }

            var nv12 = Args.ImageProvider.DummyFrame is INV12Frame;

            var settings = ServiceProvider.Get<FFmpegSettings>();

            var w = Args.ImageProvider.Width;
            var h = Args.ImageProvider.Height;

            _videoBuffer = new byte[(int)(w * h * (nv12 ? 1.5 : 4))];

            Console.WriteLine($"Video Buffer Allocated: {_videoBuffer.Length}");

            var videoPipeName = GetPipeName();

            var argsBuilder = new FFmpegArgsBuilder();

            argsBuilder.AddInputPipe(videoPipeName)
                .AddArg("thread_queue_size", 512)
                .AddArg("framerate", Args.FrameRate)
                .SetFormat("rawvideo")
                .AddArg("pix_fmt", nv12 ? "nv12" : "rgb32")
                .SetVideoSize(w, h);

            var output = argsBuilder.AddOutputFile(Args.FileName)
                .SetFrameRate(Args.FrameRate);

            Args.VideoCodec.Apply(settings, Args, output);
            
            if (settings.Resize)
            {
                var width = settings.ResizeWidth;
                var height = settings.ResizeHeight;

                if (width % 2 == 1)
                    ++width;

                if (height % 2 == 1)
                    ++height;

                output.AddArg("vf", $"scale={width}:{height}");
            }

            if (Args.AudioProvider != null)
            {
                var audioPipeName = GetPipeName();

                argsBuilder.AddInputPipe(audioPipeName)
                    .AddArg("thread_queue_size", 512)
                    .SetFormat("s16le")
                    .SetAudioCodec("pcm_s16le")
                    .SetAudioFrequency(Args.Frequency)
                    .SetAudioChannels(Args.Channels);

                Args.VideoCodec.AudioArgsProvider(Args.AudioQuality, output);

                // UpdatePeriod * Frequency * (Bytes per Second) * Channels * 2
                var audioBufferSize = (int)((1000.0 / Args.FrameRate) * 44.1 * 2 * 2 * 2);

                _audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, audioBufferSize);
            }

            _ffmpegIn = new NamedPipeServerStream(videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, _videoBuffer.Length);

            _ffmpegProcess = FFmpegService.StartFFmpeg(argsBuilder.GetArgs(), Args.FileName, out _);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Task.WaitAll(_lastFrameTask, _lastAudio);

            _ffmpegIn.Dispose();

            _audioPipe?.Dispose();

            _ffmpegProcess.WaitForExit();

            _videoBuffer = null;
        }

        /// <summary>
        /// Gets whether audio is supported.
        /// </summary>
        public bool SupportsAudio { get; } = true;

        bool _firstAudio = true;

        Task _lastAudio;

        /// <summary>
        /// Write audio block to Audio Stream.
        /// </summary>
        /// <param name="Buffer">Buffer containing audio data.</param>
        /// <param name="Length">Length of audio data in bytes.</param>
        public void WriteAudio(byte[] Buffer, int Offset, int Length)
        {
            // Might happen when writing Gif
            if (_audioPipe == null)
                return;

            if (_ffmpegProcess.HasExited)
            {
                throw new FFmpegException( _ffmpegProcess.ExitCode);
            }

            if (_firstAudio)
            {
                if (!_audioPipe.WaitForConnection(5000))
                {
                    throw new Exception("Cannot connect Audio pipe to FFmpeg");
                }

                _firstAudio = false;
            }

            if (_lastAudio == null)
            {
                _lastAudio = Task.CompletedTask;
            }

            if (!_spAudio.Wait(_spTimeout))
            {
                throw new TimeoutException();
            }

            _lastAudio = _lastAudio.ContinueWith(M =>
            {
                try
                {
                    return _audioPipe.WriteAsync(Buffer, Offset, Length);
                }
                finally
                {
                    _spAudio.Release();
                }
            });
        }

        bool _firstFrame = true;

        Task _lastFrameTask;

        /// <summary>
        /// Writes an Image frame.
        /// </summary>
        public void WriteFrame(IBitmapFrame Frame)
        {
            if (_ffmpegProcess.HasExited)
            {
                Frame.Dispose();
                throw new FFmpegException(_ffmpegProcess.ExitCode);
            }
            
            if (_firstFrame)
            {
                if (!_ffmpegIn.WaitForConnection(5000))
                {
                    throw new Exception("Cannot connect Video pipe to FFmpeg");
                }

                _firstFrame = false;
            }

            if (_lastFrameTask == null)
            {
                _lastFrameTask = Task.CompletedTask;
            }

            if (!(Frame is RepeatFrame))
            {
                using (Frame)
                {
                    if (Frame.Unwrap() is INV12Frame nv12Frame)
                    {
                        nv12Frame.CopyNV12To(_videoBuffer);
                    }
                    else Frame.CopyTo(_videoBuffer);
                }
            }

            if (!_spVideo.Wait(_spTimeout))
            {
                throw new TimeoutException();
            }

            try
            {
                _lastFrameTask = _lastFrameTask.ContinueWith(M =>
                {
                    try
                    {
                        return _ffmpegIn.WriteAsync(_videoBuffer, 0, _videoBuffer.Length);
                    }
                    finally
                    {
                        _spVideo.Release();
                    }
                });
            }
            catch (Exception e) when (_ffmpegProcess.HasExited)
            {
                throw new FFmpegException(_ffmpegProcess.ExitCode, e);
            }
        }
    }
}
