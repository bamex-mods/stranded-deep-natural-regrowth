using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace StrandedDeepNaturalRegrowth
{
    internal static class NaturalRegrowthPersistence
    {
        private const string Header = "STRANDED_DEEP_NATURAL_REGROWTH_V1";

        public static WorldEcologyState Load(string path, string expectedWorldSeed, Action<string> log)
        {
            WorldEcologyState state = new WorldEcologyState();
            state.WorldSeed = expectedWorldSeed;

            if (!File.Exists(path))
                return state;

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || !String.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
            {
                if (log != null) log("Sidecar header not recognized; starting a fresh ecology state: " + path);
                return state;
            }

            int i;
            for (i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (String.IsNullOrEmpty(line) || line[0] == '#')
                    continue;

                string[] parts = line.Split('|');
                if (parts.Length == 0)
                    continue;

                try
                {
                    if (parts[0] == "WORLD" && parts.Length >= 4)
                    {
                        string seed = Decode(parts[1]);
                        if (!String.IsNullOrEmpty(seed)) state.WorldSeed = seed;
                        state.NextPalmSequence = ParseLong(parts[2], 1);
                        state.NextBirthSequence = ParseLong(parts[3], 1);
                    }
                    else if (parts[0] == "ZONE" && parts.Length >= 5)
                    {
                        ZoneEcologyState zone = new ZoneEcologyState();
                        zone.ZoneKey = Decode(parts[1]);
                        zone.NaturalPalmCapacity = ParseInt(parts[2], 0);
                        zone.InitializedGameDay = ParseDouble(parts[3], 0.0);
                        zone.LastEcologyGameDay = ParseDouble(parts[4], zone.InitializedGameDay);
                        if (!String.IsNullOrEmpty(zone.ZoneKey))
                            state.Zones[zone.ZoneKey] = zone;
                    }
                    else if (parts[0] == "PALM" && parts.Length >= 14)
                    {
                        PalmRecord palm = new PalmRecord();
                        palm.Id = Decode(parts[1]);
                        palm.ZoneKey = Decode(parts[2]);
                        palm.NativeReference = Decode(parts[3]);
                        palm.ParentPalmId = Decode(parts[4]);
                        palm.Managed = ParseBool(parts[5]);
                        palm.Alive = ParseBool(parts[6]);
                        palm.Stage = ParseInt(parts[7], 0);
                        palm.GroundX = ParseFloat(parts[8], 0.0f);
                        palm.GroundY = ParseFloat(parts[9], 0.0f);
                        palm.GroundZ = ParseFloat(parts[10], 0.0f);
                        palm.Yaw = ParseFloat(parts[11], 0.0f);
                        palm.BirthGameDay = ParseDouble(parts[12], 0.0);
                        // v0.1o extends the existing V1 PALM row. Old sidecars ended at field 13 with a reserved 0,
                        // which naturally maps to CoconutState=unknown and is reconciled from the live palm once loaded.
                        palm.CoconutState = parts.Length >= 14 ? ParseInt(parts[13], 0) : 0;
                        palm.CoconutFruitCount = parts.Length >= 15 ? ParseInt(parts[14], 0) : 0;
                        palm.CoconutNextCheckGameDay = parts.Length >= 16 ? ParseDouble(parts[15], 0.0) : 0.0;
                        palm.CoconutCycle = parts.Length >= 17 ? ParseInt(parts[16], 0) : 0;

                        ZoneEcologyState zone;
                        if (state.Zones.TryGetValue(palm.ZoneKey, out zone) && !String.IsNullOrEmpty(palm.Id))
                            zone.Palms[palm.Id] = palm;
                    }
                    else if (parts[0] == "BIRTH" && parts.Length >= 8)
                    {
                        PendingBirthRecord birth = new PendingBirthRecord();
                        birth.Id = Decode(parts[1]);
                        birth.ZoneKey = Decode(parts[2]);
                        birth.ParentPalmId = Decode(parts[3]);
                        birth.BirthGameDay = ParseDouble(parts[4], 0.0);
                        birth.Seed = ParseInt(parts[5], 0);
                        birth.PlacementAttempts = ParseInt(parts[6], 0);

                        ZoneEcologyState zone;
                        if (state.Zones.TryGetValue(birth.ZoneKey, out zone) && !String.IsNullOrEmpty(birth.Id))
                            zone.PendingBirths.Add(birth);
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("Skipping malformed sidecar line " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                }
            }

            if (!String.Equals(state.WorldSeed, expectedWorldSeed, StringComparison.Ordinal))
            {
                if (log != null) log("Sidecar seed mismatch; expected " + expectedWorldSeed + " but file says " + state.WorldSeed + ". Starting fresh.");
                state = new WorldEcologyState();
                state.WorldSeed = expectedWorldSeed;
            }

            return state;
        }

        public static void SaveAtomic(string path, WorldEcologyState state)
        {
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string tmp = path + ".tmp";
            string bak = path + ".bak";
            StringBuilder sb = new StringBuilder(32768);
            sb.AppendLine(Header);
            sb.AppendLine("WORLD|" + Encode(state.WorldSeed) + "|" + state.NextPalmSequence.ToString(CultureInfo.InvariantCulture) + "|" + state.NextBirthSequence.ToString(CultureInfo.InvariantCulture));

            List<string> zoneKeys = new List<string>(state.Zones.Keys);
            zoneKeys.Sort(StringComparer.Ordinal);
            int z;
            for (z = 0; z < zoneKeys.Count; z++)
            {
                ZoneEcologyState zone = state.Zones[zoneKeys[z]];
                sb.AppendLine("ZONE|" + Encode(zone.ZoneKey) + "|" +
                    zone.NaturalPalmCapacity.ToString(CultureInfo.InvariantCulture) + "|" +
                    zone.InitializedGameDay.ToString("R", CultureInfo.InvariantCulture) + "|" +
                    zone.LastEcologyGameDay.ToString("R", CultureInfo.InvariantCulture));

                List<string> palmKeys = new List<string>(zone.Palms.Keys);
                palmKeys.Sort(StringComparer.Ordinal);
                int p;
                for (p = 0; p < palmKeys.Count; p++)
                {
                    PalmRecord palm = zone.Palms[palmKeys[p]];
                    sb.AppendLine("PALM|" + Encode(palm.Id) + "|" + Encode(palm.ZoneKey) + "|" + Encode(palm.NativeReference) + "|" + Encode(palm.ParentPalmId) + "|" +
                        Bool(palm.Managed) + "|" + Bool(palm.Alive) + "|" + palm.Stage.ToString(CultureInfo.InvariantCulture) + "|" +
                        palm.GroundX.ToString("R", CultureInfo.InvariantCulture) + "|" + palm.GroundY.ToString("R", CultureInfo.InvariantCulture) + "|" + palm.GroundZ.ToString("R", CultureInfo.InvariantCulture) + "|" +
                        palm.Yaw.ToString("R", CultureInfo.InvariantCulture) + "|" + palm.BirthGameDay.ToString("R", CultureInfo.InvariantCulture) + "|" +
                        palm.CoconutState.ToString(CultureInfo.InvariantCulture) + "|" + palm.CoconutFruitCount.ToString(CultureInfo.InvariantCulture) + "|" +
                        palm.CoconutNextCheckGameDay.ToString("R", CultureInfo.InvariantCulture) + "|" + palm.CoconutCycle.ToString(CultureInfo.InvariantCulture));
                }

                int b;
                for (b = 0; b < zone.PendingBirths.Count; b++)
                {
                    PendingBirthRecord birth = zone.PendingBirths[b];
                    sb.AppendLine("BIRTH|" + Encode(birth.Id) + "|" + Encode(birth.ZoneKey) + "|" + Encode(birth.ParentPalmId) + "|" +
                        birth.BirthGameDay.ToString("R", CultureInfo.InvariantCulture) + "|" + birth.Seed.ToString(CultureInfo.InvariantCulture) + "|" +
                        birth.PlacementAttempts.ToString(CultureInfo.InvariantCulture) + "|0");
                }
            }

            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, bak, true);
                    return;
                }
                catch
                {
                    File.Copy(path, bak, true);
                    File.Delete(path);
                }
            }
            File.Move(tmp, path);
        }

        private static string Bool(bool value) { return value ? "1" : "0"; }
        private static bool ParseBool(string value) { return value == "1" || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase); }
        private static int ParseInt(string value, int fallback) { int v; return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback; }
        private static long ParseLong(string value, long fallback) { long v; return Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback; }
        private static double ParseDouble(string value, double fallback) { double v; return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback; }
        private static float ParseFloat(string value, float fallback) { float v; return Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback; }

        private static string Encode(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            if (String.IsNullOrEmpty(value) || value == "-") return String.Empty;
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
    }
}
