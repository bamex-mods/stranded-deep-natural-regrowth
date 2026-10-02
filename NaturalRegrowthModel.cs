using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrandedDeepNaturalRegrowth
{
    internal sealed class WorldEcologyState
    {
        public string WorldSeed = "unknown-world";
        public long NextPalmSequence = 1;
        public long NextBirthSequence = 1;
        public readonly Dictionary<string, ZoneEcologyState> Zones = new Dictionary<string, ZoneEcologyState>(StringComparer.Ordinal);
    }

    internal sealed class ZoneEcologyState
    {
        public string ZoneKey = String.Empty;
        public int NaturalPalmCapacity;
        public double InitializedGameDay;
        public double LastEcologyGameDay;
        public readonly Dictionary<string, PalmRecord> Palms = new Dictionary<string, PalmRecord>(StringComparer.Ordinal);
        public readonly List<PendingBirthRecord> PendingBirths = new List<PendingBirthRecord>();
    }

    internal sealed class PalmRecord
    {
        public string Id = String.Empty;
        public string ZoneKey = String.Empty;
        public string NativeReference = String.Empty;
        public string ParentPalmId = String.Empty;
        public bool Managed;
        public bool Alive = true;
        public int Stage;
        public float GroundX;
        public float GroundY;
        public float GroundZ;
        public float Yaw;
        public double BirthGameDay;

        // Coconut lifecycle persisted in the NaturalRegrowth sidecar.
        // 0=unknown/uninitialized, 1=fruiting, 2=empty/waiting, 3=regrowth succeeded/pending native materialization.
        public int CoconutState;
        public int CoconutFruitCount;
        public double CoconutNextCheckGameDay;
        public int CoconutCycle;

        public Vector3 GroundPosition
        {
            get { return new Vector3(GroundX, GroundY, GroundZ); }
            set
            {
                GroundX = value.x;
                GroundY = value.y;
                GroundZ = value.z;
            }
        }
    }

    internal sealed class PendingBirthRecord
    {
        public string Id = String.Empty;
        public string ZoneKey = String.Empty;
        public string ParentPalmId = String.Empty;
        public double BirthGameDay;
        public int Seed;
        public int PlacementAttempts;
    }

    internal sealed class ZoneObservation
    {
        public int LastChildCount = -1;
        public int LastPalmCount = -1;
        public float StableSinceRealtime;
        public bool WasReady;
        public bool WasLoadedLastScan;
        public int LoadGeneration;
        public readonly HashSet<string> ManagedSeenThisLoad = new HashSet<string>(StringComparer.Ordinal);
    }

    public sealed class NaturalRegrowthManagedPalmTag : MonoBehaviour
    {
        public string PalmId = String.Empty;
        public string ZoneKey = String.Empty;
    }

    public sealed class NaturalRegrowthManagedFruitTag : MonoBehaviour
    {
        public string PalmId = String.Empty;
        public string ZoneKey = String.Empty;
        public bool Detached;
    }
}
