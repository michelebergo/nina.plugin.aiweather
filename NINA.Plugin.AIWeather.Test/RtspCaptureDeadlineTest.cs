using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Services;
using Emgu.CV;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// One stuck frame grab held the check gate for 13 minutes and the data-age fail-safe
    /// parked a rig under a clear sky (#21, night of 25-26/10): FFmpeg gives a read 30 s by
    /// default and the capture retried it 25 times. A capture must give up well inside one
    /// check interval, whatever the camera does.
    /// </summary>
    [TestFixture]
    public class RtspCaptureDeadlineTest
    {
        [Test]
        public async Task AStreamThatAcceptsTheConnectionAndNeverAnswersIsGivenUpInsideTheOpenTimeout() {
            // A camera (or a camera busy with another client) that completes the TCP handshake
            // and then says nothing to OPTIONS/DESCRIBE.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var held = new System.Collections.Generic.List<TcpClient>();
            var accepting = Task.Run(async () => {
                try { while (true) held.Add(await listener.AcceptTcpClientAsync()); }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            });

            try
            {
                using var service = new RtspCaptureService();
                var watch = Stopwatch.StartNew();
                var opening = service.InitializeAsync($"rtsp://127.0.0.1:{port}/stalled");
                var finished = await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(60)));
                watch.Stop();

                finished.Should().BeSameAs(opening, "the open must come back on its own, not hang until the test gives up");
                (await opening).Should().BeFalse();
                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(RtspCaptureService.OpenTimeoutSeconds + 5),
                    "the open timeout is the plugin's own, not FFmpeg's 30 s default");
            }
            finally
            {
                listener.Stop();
                foreach (var c in held) c.Dispose();
                await accepting;
            }
        }

        [Test]
        public void ReadsStopWhenTheCaptureBudgetIsSpentNotAfterTwentyFiveTries() {
            // Each read takes a second and yields nothing: the stream is up, the frames are not
            // coming. Twenty-five of those is Dennis's 13 minutes in miniature.
            var reads = 0;
            bool SlowEmptyRead(Mat _) { reads++; Thread.Sleep(1000); return false; }

            var watch = Stopwatch.StartNew();
            var frame = RtspCaptureService.ReadFrameWithinBudget(SlowEmptyRead, TimeSpan.FromSeconds(3), CancellationToken.None);
            watch.Stop();

            frame.Should().BeNull();
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            reads.Should().BeLessThanOrEqualTo(4);
        }
    }
}
