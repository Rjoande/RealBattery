using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealBattery
{
    internal static class KopernicusStarResolver
    {
        // Cache per bodyName -> (starBodyName, luminosity, success)
        private static readonly Dictionary<string, (string starName, double luminosity, bool ok)> _cache
            = new Dictionary<string, (string, double, bool)>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized = false;
        private static Dictionary<string, ConfigNode> _kopBodyByName; // bodyName -> Kopernicus Body node

        /// <summary>
        /// Try resolving the "correct Sun" for a given body by walking parents and checking Kopernicus template=Sun.
        /// If found, also reads ScaledVersion/Light/luminosity (defaults to 1.0 if missing).
        /// Returns false if Kopernicus data is missing or the chain has no template=Sun body.
        /// </summary>
        public static bool TryResolveStar(CelestialBody startBody, out CelestialBody starBody, out double luminosity)
        {
            starBody = null;
            luminosity = 1.0;

            if (startBody == null) return false;

            // Fast path: cached per startBody
            if (_cache.TryGetValue(startBody.bodyName, out var c))
            {
                if (!c.ok) return false;

                starBody = FlightGlobals.GetBodyByName(c.starName);
                luminosity = c.luminosity;
                return (starBody != null);
            }

            EnsureIndex();

            // If we cannot build the Kopernicus index, fallback
            if (_kopBodyByName == null || _kopBodyByName.Count == 0)
            {
                _cache[startBody.bodyName] = (null, 1.0, false);
                return false;
            }

            // Walk parents: body -> referenceBody -> ... until null
            CelestialBody cur = startBody;
            while (cur != null)
            {
                if (_kopBodyByName.TryGetValue(cur.bodyName, out var kopBodyNode))
                {
                    // Check Template/name == "Sun"
                    var template = kopBodyNode.GetNode("Template");
                    if (template != null)
                    {
                        var tName = template.GetValue("name");
                        if (!string.IsNullOrEmpty(tName) && string.Equals(tName, "Sun", StringComparison.OrdinalIgnoreCase))
                        {
                            // Found star candidate
                            starBody = cur;

                            // Read luminosity (optional; default 1.0)
                            luminosity = ReadLuminosityOrDefault(kopBodyNode, 1.0);

                            _cache[startBody.bodyName] = (starBody.bodyName, luminosity, true);
                            return true;
                        }
                    }
                }

                cur = cur.referenceBody;
            }

            Debug.Log($"[RealBattery] [KopernicusStarResolver] Resolved star {starBody.bodyName} with {luminosity}x luminosity");

            // Not found
            _cache[startBody.bodyName] = (null, 1.0, false);
            return false;
        }

        private static void EnsureIndex()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                // Build a lookup: bodyName -> Kopernicus Body config node
                _kopBodyByName = new Dictionary<string, ConfigNode>(StringComparer.OrdinalIgnoreCase);

                // Kopernicus configs are stored in GameDatabase as config nodes.
                // We search for "Kopernicus" roots and extract all "Body" subnodes.
                var roots = GameDatabase.Instance.GetConfigNodes("Kopernicus");
                if (roots == null || roots.Length == 0) return;

                foreach (var root in roots)
                {
                    if (root == null) continue;

                    var bodies = root.GetNodes("Body");
                    if (bodies == null) continue;

                    foreach (var b in bodies)
                    {
                        if (b == null) continue;

                        // Kopernicus Body name is usually "name = <CelestialBodyName>"
                        var bodyName = b.GetValue("name");
                        if (string.IsNullOrEmpty(bodyName)) continue;

                        // Keep the first match; multiple packs should not redefine same body
                        if (!_kopBodyByName.ContainsKey(bodyName))
                            _kopBodyByName[bodyName] = b;
                    }
                }
            }
            catch (Exception ex)
            {
                _kopBodyByName = null;
                RBLog.Warn($"[KopernicusStarResolver] Failed to build Kopernicus index: {ex.Message}");
            }
        }

        private static double ReadLuminosityOrDefault(ConfigNode kopBodyNode, double def)
        {
            try
            {
                var scaled = kopBodyNode.GetNode("ScaledVersion");
                var light = scaled?.GetNode("Light");
                var lumStr = light?.GetValue("luminosity");
                if (!string.IsNullOrEmpty(lumStr) && double.TryParse(lumStr, out var lum) && lum > 0.0)
                    return lum;
            }
            catch { /* ignore */ }

            return def;
        }
    }
}