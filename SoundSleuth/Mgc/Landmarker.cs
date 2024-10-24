﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundSleuth.Mgc
{
    internal class Landmarker
    {
        public const int RADIUS_TIME = 47;
        public const int RADIUS_FREQ = 9;

        private const int RATE = 12;

        private static readonly IReadOnlyList<int> BandFreqs = new[] {250,520,1450,3500,5500 };
        private static readonly int MinBin = Math.Max(Analyser.FreqToBin(BandFreqs.Min()), RADIUS_FREQ);
        private static readonly int MaxBin = Math.Min(Analyser.FreqToBin(BandFreqs.Max()), Analyser.BIN_COUNT - RADIUS_FREQ);

        private static readonly float MinMagnitudeSquared = 1f / 512 / 512;

        private static readonly float LogMinMagnitudeSquared = MathF.Log(MinMagnitudeSquared);
        private readonly Analyser _analysis;
        private readonly IReadOnlyList<List<Landmark>> _bands;
        public Landmarker(Analyser analysis)
        {
            _analysis = analysis;
            _bands = Enumerable.Range(0, BandFreqs.Count - 1).Select(_ => new List<Landmark>()).ToList();
        }
        public void Find(int stripe) 
        {
            for(var bin = MinBin; bin < MaxBin; bin++)
            {
                if (_analysis.GetMagnitudeSquared(stripe, bin) < MinMagnitudeSquared) continue;
                if (!IsPeak(stripe, bin, RADIUS_TIME, 0)) continue;
                if (!IsPeak(stripe, bin, 3, RADIUS_FREQ)) continue;
                AddLandmarkAt(stripe, bin);
            }
        }
        public  IEnumerable<IEnumerable<Landmark>> EnumerateBandedLandmarks()
        {
            return _bands;
        }
        public IEnumerable<Landmark> EnumerateAllLandmarks()
        {
            return _bands.SelectMany(i => i);
        }

        private static int GetBandIndex(float bin)
        {
            var freq = Analyser.BinToFreq(bin);
            if (freq < BandFreqs[0]) return - 1;
            for (var i = 1; i < BandFreqs.Count; i++) if (freq < BandFreqs[i]) return i - 1;
            return -1;
        }
        private Landmark CreateLandmarkAt(int stripe, int bin)
        { 
            var alpha = GetLogMagnitude(stripe, bin - 1);
            var beta = GetLogMagnitude(stripe, bin);
            var gamma = GetLogMagnitude(stripe, bin + 1);
            var p = (alpha - gamma) / (alpha - 2 * beta + gamma) / 2;

            return new Landmark(stripe, bin + p, beta - (alpha - gamma) * p / 4);
        }

        private float GetLogMagnitude(int stripe, int bin)
        {
            return 18 * 1024 * (1 - (MathF.Log(_analysis.GetMagnitudeSquared(stripe, bin)) / LogMinMagnitudeSquared));
        }

        private bool IsPeak(int stripe, int bin, int stripeRadius, int binRadius)
        {
            var center = _analysis.GetMagnitudeSquared(stripe, bin);
            for (var s = -stripeRadius; s <= stripeRadius; s++)
            {
                for (var b = -binRadius; b <= binRadius; b++)
                {
                    if (s == 0 && b == 0) continue;
                    if (_analysis.GetMagnitudeSquared(stripe + s, bin + b) >= center) return false;
                }
            }
            return true;
        }

        private void AddLandmarkAt(int stripe, int bin)
        {
            var newLandmark = CreateLandmarkAt(stripe, bin);

            var bandIndex = GetBandIndex(newLandmark.InterpolatedBin);
            if (bandIndex < 0) return;

            var bandLandmarks = _bands[bandIndex];

            if (bandLandmarks.Any())
            {
                var capturedDuration = 1d / Analyser.CHUNKS_PER_SECOND * (stripe - bandLandmarks.First().StripeIndex);
                var allowedCount = 1 + capturedDuration * RATE;
                if (bandLandmarks.Count > allowedCount)
                {
                    var pruneIndex = bandLandmarks.FindLastIndex(l => l.InterpolatedLogMagnitude < newLandmark.InterpolatedLogMagnitude);
                    if (pruneIndex < 0) return;

                    bandLandmarks.RemoveAt(pruneIndex);
                }
            }

            bandLandmarks.Add(newLandmark);
        }
    }
}
