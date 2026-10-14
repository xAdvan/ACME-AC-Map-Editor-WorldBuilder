using System;
using System.Collections.Generic;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class HeightMapGenerator {
        private struct Landmass {
            public float CenterX, CenterY, Radius;
            public bool IsContinent;
            public float NoiseOfsX, NoiseOfsY;
            public float Angle;
            public float Elongation;
        }

        // Land is any vertex whose landmass influence exceeds this.
        private const float LandThreshold = 0.02f;

        // Coastal shaping, in vertices (one vertex = 24 world units).
        // Target rise per vertex on walkable coasts; AC's walkable limit is ~48°
        // (≈27 units per vertex), so this keeps beaches comfortably walkable.
        private const float WalkableRisePerVertex = 12f;
        private const float MinGentleRamp = 4f;
        private const float MaxGentleRamp = 48f;
        private const float CliffRamp = 1.5f;
        private const float ShelfWidth = 12f;

        /// <summary>
        /// Generates an elevation map with distinct continents and islands.
        /// Uses domain warping and per-landmass noise for organic, non-circular shapes.
        /// Coastlines rise gradually out of the sea along most of their length, with
        /// occasional stretches of sea cliffs.
        /// </summary>
        public static float[,] Generate(WorldGeneratorParams p, SimplexNoise noise, int verticesX, int verticesY, Random rng, float[] landHeightTable) {
            var landmasses = PlaceLandmasses(p, verticesX, verticesY, rng);
            var map = new float[verticesX, verticesY];
            var isLand = new bool[verticesX, verticesY];
            int noiseOffset = p.Seed * 1000;
            float seaLevel = SeaLevelNormalized(p.SeaLevelIndex, landHeightTable);

            // Edge falloff to prevent continent overflow beyond world boundary
            int edgeMargin = Math.Max(verticesX, verticesY) / 20;

            // Pass 1: inland elevation (the shape away from the coast) and the land mask.
            for (int x = 0; x < verticesX; x++) {
                for (int y = 0; y < verticesY; y++) {
                    float wx = (p.StartX * 8 + x) * 0.005f + noiseOffset;
                    float wy = (p.StartY * 8 + y) * 0.005f + noiseOffset;

                    float landInfluence = ComputeLandInfluence(x, y, landmasses, noise);

                    int edgeDist = Math.Min(Math.Min(x, verticesX - 1 - x), Math.Min(y, verticesY - 1 - y));
                    if (edgeDist < edgeMargin)
                        landInfluence *= (float)edgeDist / edgeMargin;

                    if (landInfluence > LandThreshold) {
                        float detail = noise.FBM(wx * 2f, wy * 2f, 6, 0.45f + p.Roughness * 0.1f, 2.1f);
                        float ridged = noise.RidgedNoise(wx * 1.5f + 500f, wy * 1.5f + 500f, 4, 0.5f, 2f);
                        float terrainVariation = 0.5f + detail * 0.25f + ridged * 0.2f;
                        map[x, y] = Math.Clamp(0.12f + landInfluence * 0.65f * terrainVariation, 0f, 1f);
                        isLand[x, y] = true;
                    }
                }
            }

            // Pass 2: blend the inland shape down to sea level near the coast, using the
            // true distance to the shoreline so slopes are controlled in world units.
            var distToSea = DistanceTransform(isLand, sourceIsLand: false);
            var distToLand = DistanceTransform(isLand, sourceIsLand: true);

            float heightRange = landHeightTable[^1] - landHeightTable[0];
            float mountainScale = Math.Max(p.MountainScale, 0.1f);
            // Height of the shoreline itself: just above the waterline so the beach is dry land.
            float shoreHeight = seaLevel + 0.004f;
            // Kept small so float precision holds for any seed.
            float coastOfs = (p.Seed % 1000) * 37f;

            for (int x = 0; x < verticesX; x++) {
                for (int y = 0; y < verticesY; y++) {
                    float gx = p.StartX * 8 + x;
                    float gy = p.StartY * 8 + y;

                    if (isLand[x, y]) {
                        float d = distToSea[x, y];
                        float inland = map[x, y];
                        if (d >= MaxGentleRamp + 2f || inland <= shoreHeight) continue;

                        // Low-frequency noise picks which stretches of coast are cliffs,
                        // with a band of steep-but-not-sheer coast between the two.
                        float cliffNoise = (noise.FBM(gx * 0.008f + coastOfs + 9000f, gy * 0.008f + coastOfs + 9000f, 3, 0.5f, 2f) + 1f) * 0.5f;
                        float cliffiness = SmoothStep(0.58f, 0.72f, cliffNoise);

                        // Ramp wide enough that the rise from shore to inland stays walkable.
                        float riseUnits = (inland - shoreHeight) * heightRange * mountainScale;
                        float gentleRamp = Math.Clamp(riseUnits * 1.5f / WalkableRisePerVertex, MinGentleRamp, MaxGentleRamp);
                        float widthVar = (noise.FBM(gx * 0.01f + coastOfs + 4000f, gy * 0.01f + coastOfs + 4000f, 2, 0.5f, 2f) + 1f) * 0.5f;
                        gentleRamp = Math.Min(gentleRamp * (1f + widthVar * 0.5f), MaxGentleRamp);

                        float ramp = Lerp(gentleRamp, CliffRamp, cliffiness);
                        // Cliffs keep a narrow strip of beach at their foot.
                        float beach = cliffiness * 1.5f;

                        float w = SmoothStep(0f, ramp, d - beach);
                        map[x, y] = Lerp(shoreHeight, inland, w);
                    } else {
                        // Continental shelf: shallow near land, deepening offshore.
                        float d = distToLand[x, y];
                        float detail = noise.FBM(gx * 0.01f + coastOfs + 2000f, gy * 0.01f + coastOfs + 2000f, 3, 0.5f, 2f);
                        float floor = seaLevel * (0.3f + 0.15f * (detail + 1f));
                        float w = SmoothStep(0f, ShelfWidth, d);
                        map[x, y] = Lerp(seaLevel * 0.9f, floor, w);
                    }

                    map[x, y] = Math.Clamp(map[x, y], 0f, 1f);
                }
            }

            return map;
        }

        /// <summary>
        /// Two-pass chamfer distance transform. Returns, for every vertex, the distance
        /// (in vertices) to the nearest vertex whose land state equals <paramref name="sourceIsLand"/>.
        /// Vertices of the source kind get 0.
        /// </summary>
        private static float[,] DistanceTransform(bool[,] isLand, bool sourceIsLand) {
            int w = isLand.GetLength(0), h = isLand.GetLength(1);
            const float Ortho = 1f, Diag = 1.41421356f;
            var dist = new float[w, h];

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    dist[x, y] = isLand[x, y] == sourceIsLand ? 0f : float.MaxValue;

            // Forward pass
            for (int x = 0; x < w; x++) {
                for (int y = 0; y < h; y++) {
                    float v = dist[x, y];
                    if (v == 0f) continue;
                    if (x > 0) {
                        v = Math.Min(v, dist[x - 1, y] + Ortho);
                        if (y > 0) v = Math.Min(v, dist[x - 1, y - 1] + Diag);
                        if (y < h - 1) v = Math.Min(v, dist[x - 1, y + 1] + Diag);
                    }
                    if (y > 0) v = Math.Min(v, dist[x, y - 1] + Ortho);
                    dist[x, y] = v;
                }
            }

            // Backward pass
            for (int x = w - 1; x >= 0; x--) {
                for (int y = h - 1; y >= 0; y--) {
                    float v = dist[x, y];
                    if (v == 0f) continue;
                    if (x < w - 1) {
                        v = Math.Min(v, dist[x + 1, y] + Ortho);
                        if (y > 0) v = Math.Min(v, dist[x + 1, y - 1] + Diag);
                        if (y < h - 1) v = Math.Min(v, dist[x + 1, y + 1] + Diag);
                    }
                    if (y < h - 1) v = Math.Min(v, dist[x, y + 1] + Ortho);
                    dist[x, y] = v;
                }
            }

            return dist;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float SmoothStep(float edge0, float edge1, float x) {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Generates a moisture map for biome selection. Separate noise domain.
        /// </summary>
        public static float[,] GenerateMoisture(WorldGeneratorParams p, SimplexNoise noise, int verticesX, int verticesY) {
            var map = new float[verticesX, verticesY];
            int offset = p.Seed * 1000 + 50000;

            for (int x = 0; x < verticesX; x++) {
                for (int y = 0; y < verticesY; y++) {
                    float wx = (p.StartX * 8 + x) * 0.003f + offset;
                    float wy = (p.StartY * 8 + y) * 0.003f + offset;

                    float moisture = noise.FBM(wx, wy, 4, 0.5f, 2f);
                    moisture = (moisture + 1f) * 0.5f;
                    map[x, y] = Math.Clamp(moisture, 0f, 1f);
                }
            }

            return map;
        }

        public static byte ElevationToHeightIndex(float elevation, float[] landHeightTable) {
            float minH = landHeightTable[0];
            float maxH = landHeightTable[landHeightTable.Length - 1];
            float targetZ = minH + elevation * (maxH - minH);

            // Binary search for the nearest entry (table is sorted ascending).
            int lo = 0, hi = landHeightTable.Length - 1;
            while (lo < hi) {
                int mid = (lo + hi) >> 1;
                if (landHeightTable[mid] < targetZ) lo = mid + 1;
                else hi = mid;
            }
            // lo is the first index >= targetZ; compare with lo-1 to find the closest.
            if (lo > 0 && MathF.Abs(landHeightTable[lo - 1] - targetZ) <= MathF.Abs(landHeightTable[lo] - targetZ))
                lo--;
            return (byte)lo;
        }

        public static float SeaLevelNormalized(int seaLevelIndex, float[] landHeightTable) {
            float minH = landHeightTable[0];
            float maxH = landHeightTable[landHeightTable.Length - 1];
            float seaZ = landHeightTable[Math.Clamp(seaLevelIndex, 0, landHeightTable.Length - 1)];
            return (seaZ - minH) / (maxH - minH);
        }

        private static float ComputeLandInfluence(int x, int y, List<Landmass> landmasses, SimplexNoise noise) {
            float maxInfluence = 0f;
            foreach (var lm in landmasses) {
                float dx = x - lm.CenterX;
                float dy = y - lm.CenterY;

                // Domain warp: displace the vector itself for organic blob shapes.
                // Each landmass uses its own noise offset so every shape is unique.
                float warpScale = lm.IsContinent ? 0.007f : 0.012f;
                float warpStrength = lm.Radius * 0.55f;
                float wx = noise.FBM(x * warpScale + lm.NoiseOfsX,
                                     y * warpScale + lm.NoiseOfsY, 4, 0.55f, 2f);
                float wy = noise.FBM(x * warpScale + lm.NoiseOfsX + 300f,
                                     y * warpScale + lm.NoiseOfsY + 300f, 4, 0.55f, 2f);
                dx += wx * warpStrength;
                dy += wy * warpStrength;

                // Rotate into the landmass's local frame and apply elongation
                float cosA = MathF.Cos(lm.Angle);
                float sinA = MathF.Sin(lm.Angle);
                float localX = dx * cosA + dy * sinA;
                float localY = -dx * sinA + dy * cosA;
                localX /= lm.Elongation;

                float dist = MathF.Sqrt(localX * localX + localY * localY);

                // Fine coastline noise for bays and peninsulas
                float coastFreq = lm.IsContinent ? 0.025f : 0.04f;
                float coastAmp = lm.Radius * 0.12f;
                float coastNoise = noise.FBM(x * coastFreq + lm.NoiseOfsX + 700f,
                                             y * coastFreq + lm.NoiseOfsY + 700f, 3, 0.5f, 2.2f);
                dist += coastNoise * coastAmp;

                if (dist >= lm.Radius) continue;

                float t = 1f - dist / lm.Radius;
                t = t * t * (3f - 2f * t);
                maxInfluence = Math.Max(maxInfluence, t);
            }
            return maxInfluence;
        }

        private static List<Landmass> PlaceLandmasses(WorldGeneratorParams p, int verticesX, int verticesY, Random rng) {
            var result = new List<Landmass>();
            float area = (float)verticesX * verticesY;
            float coverageMul = 0.5f + p.LandCoverage;

            float continentRadius = 0;
            if (p.ContinentCount > 0) {
                float targetArea = area * 0.45f / p.ContinentCount;
                continentRadius = MathF.Sqrt(targetArea / MathF.PI) * coverageMul;
            }

            float islandBaseRadius;
            if (p.ContinentCount == 0 && p.IslandCount > 0) {
                float targetArea = area * 0.25f / p.IslandCount;
                islandBaseRadius = MathF.Sqrt(targetArea / MathF.PI) * coverageMul;
            } else {
                islandBaseRadius = Math.Max(continentRadius * 0.18f, 15f) * coverageMul;
            }

            for (int i = 0; i < p.ContinentCount; i++) {
                float r = continentRadius * (0.8f + (float)rng.NextDouble() * 0.4f);
                var (cx, cy) = FindCenter(result, r, verticesX, verticesY, rng, r * 0.3f);
                result.Add(new Landmass {
                    CenterX = cx, CenterY = cy, Radius = r, IsContinent = true,
                    NoiseOfsX = (float)rng.NextDouble() * 10000f,
                    NoiseOfsY = (float)rng.NextDouble() * 10000f,
                    Angle = (float)(rng.NextDouble() * Math.PI * 2),
                    Elongation = 1.3f + (float)rng.NextDouble() * 0.9f
                });
            }

            for (int i = 0; i < p.IslandCount; i++) {
                float r = islandBaseRadius * (0.5f + (float)rng.NextDouble() * 1.0f);
                var (cx, cy) = FindCenter(result, r, verticesX, verticesY, rng, r * 0.5f);
                result.Add(new Landmass {
                    CenterX = cx, CenterY = cy, Radius = r, IsContinent = false,
                    NoiseOfsX = (float)rng.NextDouble() * 10000f,
                    NoiseOfsY = (float)rng.NextDouble() * 10000f,
                    Angle = (float)(rng.NextDouble() * Math.PI * 2),
                    Elongation = 1.0f + (float)rng.NextDouble() * 0.6f
                });
            }

            return result;
        }

        private static (float x, float y) FindCenter(
            List<Landmass> existing, float radius,
            int w, int h, Random rng, float minSpacing) {

            float margin = Math.Max(radius * 0.5f, Math.Min(w, h) * 0.15f);

            for (int attempt = 0; attempt < 200; attempt++) {
                float x = margin + (float)rng.NextDouble() * (w - 2f * margin);
                float y = margin + (float)rng.NextDouble() * (h - 2f * margin);

                bool valid = true;
                foreach (var lm in existing) {
                    float dx = x - lm.CenterX;
                    float dy = y - lm.CenterY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float minDist = (radius + lm.Radius) * 0.55f + minSpacing;
                    if (dist < minDist) { valid = false; break; }
                }

                if (valid) return (x, y);
            }

            float fx = margin + (float)rng.NextDouble() * (w - 2f * margin);
            float fy = margin + (float)rng.NextDouble() * (h - 2f * margin);
            return (fx, fy);
        }
    }
}
