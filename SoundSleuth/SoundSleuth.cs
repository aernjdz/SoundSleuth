﻿using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundSleuth.Mgc;

namespace SoundSleuth
{
    internal static class Shazam
	{
        private static readonly MMDeviceEnumerator Enumerator = new();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly string DeviceId = Guid.NewGuid().ToString();
		public static async Task<SoundSleuthMatch> IdentifyAsync(string deviceId, CancellationToken cancel)
		{
			var device = Enumerator.GetDevice(deviceId);
			if (device == null || device.State != DeviceState.Active) throw new ArgumentException("Selected device not available");

			var capture = device.DataFlow switch
			{
				DataFlow.Capture => new WasapiCapture(device),
				DataFlow.Render => new WasapiLoopbackCapture(device)
			};

			var buffer = new BufferedWaveProvider(capture.WaveFormat) { ReadFully = false, DiscardOnBufferOverflow = true };
			using var resampler = new MediaFoundationResampler(buffer, new WaveFormat(16000, 16, 1));
			var samples = resampler.ToSampleProvider();

			capture.DataAvailable += (_, e) => { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); };
			capture.StartRecording();

			var analyser = new Analyser();
			var landmarker = new Landmarker(analyser);

			var retryMs = 3000;

			while (true)
			{
				if (cancel.IsCancellationRequested)
				{
					capture.StopRecording();
					throw new OperationCanceledException();
				}

				if (buffer.BufferedDuration.TotalSeconds < 1)
				{
					Thread.Sleep(100);
					continue;
				}

				analyser.ReadChunk(samples);

				if (analyser.StripeCount > 2 * Landmarker.RADIUS_TIME) landmarker.Find(analyser.StripeCount - Landmarker.RADIUS_TIME - 1);
				if (analyser.ProcessedMs < retryMs) continue;

				var body = new ShazamRequest
				{
					Signature = new ShazamSignature
					{
						Uri = "data:audio/vnd.shazam.sig;base64," + Convert.ToBase64String(Signature.Create(Analyser.SAMPLE_RATE, analyser.ProcessedSamples, landmarker)),
						SampleMs = analyser.ProcessedMs
					}
				};

				using var res = await Http.PostAsync($"https://amp.shazam.com/discovery/v5/en/US/android/-/tag/{DeviceId}/{Guid.NewGuid()}", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), cancel);
				var data = JsonSerializer.Deserialize<ShazamResponse>(await res.Content.ReadAsStringAsync(cancel));

				if (data.RetryMs > 0)
				{
					retryMs = (int)data.RetryMs;
					continue;
				}

				capture.StopRecording();
				if (data.Track == null) return null;
				return new SoundSleuthMatch
				{
					Title = data.Track.Title,
					Artist = data.Track.Subtitle,
					Link = data.Track.Share.Link,
					Cover = data.Track?.Images?.CoverHQ ?? data.Track?.Images?.Cover ?? data.Track.Share.Image
				};
			}
		}

	}
}
