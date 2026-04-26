using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Impulse.Core.Map;
using Impulse.Core.Players;

namespace Impulse.Core.Engine;

// Saveable snapshot of a game-in-progress. Stores only dynamic state +
// the Seed/PlayerCount used to bootstrap the deterministic parts (Map,
// CardsById, race assignments). On load, SetupFactory.NewGame is invoked
// with the same seed/count and the snapshot's dynamic state overwrites
// the freshly-built one. Mid-effect saves are NOT supported — saving is
// only safe between phases (no PendingEffect, no plan-resolution flag).
public sealed class GameSnapshot
{
    public int Version { get; set; } = 1;
    public int Seed { get; set; }
    public int PlayerCount { get; set; }
    public string[] AiPolicies { get; set; } = Array.Empty<string>();
    public int CurrentTurn { get; set; }
    public int ActivePlayer { get; set; }
    public string Phase { get; set; } = "AddImpulse";
    public bool IsGameOver { get; set; }
    public bool HomePicksDone { get; set; }
    public int ImpulseCursor { get; set; }
    public List<int> Deck { get; set; } = new();
    public List<int> Discard { get; set; } = new();
    public List<int> Impulse { get; set; } = new();
    public List<ShipDto> Ships { get; set; } = new();
    public List<NodeCardDto> NodeCards { get; set; } = new();
    public List<PlayerDto> Players { get; set; } = new();

    public sealed class ShipDto
    {
        public int Owner { get; set; }
        public string Kind { get; set; } = "node"; // "node" or "gate"
        public int Id { get; set; }                // node or gate id
    }

    public sealed class NodeCardDto
    {
        public int NodeId { get; set; }
        public string State { get; set; } = "core"; // "core" | "down" | "up"
        public int? CardId { get; set; }
    }

    public sealed class PlayerDto
    {
        public int Id { get; set; }
        public int RaceId { get; set; }
        public int Prestige { get; set; }
        public int ShipsAvailable { get; set; }
        public List<int> Hand { get; set; } = new();
        public List<int> Plan { get; set; } = new();
        public List<int>? NextPlan { get; set; }
        public List<int> Minerals { get; set; } = new();
        public TechDto Left { get; set; } = new();
        public TechDto Right { get; set; } = new();
    }

    public sealed class TechDto
    {
        public string Kind { get; set; } = "basic_common"; // "researched"|"basic_common"|"basic_unique"
        public int? CardId { get; set; }
        public int? RaceId { get; set; }
    }

    // Compact: no whitespace, drop nulls. Default file is ~2–4 KB for a
    // mid-game 4-player session, well under what JSON could be otherwise.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static GameSnapshot Capture(GameState g, int seed, int playerCount, IReadOnlyList<string> aiPolicies)
    {
        var snap = new GameSnapshot
        {
            Seed = seed,
            PlayerCount = playerCount,
            AiPolicies = aiPolicies.ToArray(),
            CurrentTurn = g.CurrentTurn,
            ActivePlayer = g.ActivePlayer.Value,
            Phase = g.Phase.ToString(),
            IsGameOver = g.IsGameOver,
            HomePicksDone = g.HomePicksDone,
            ImpulseCursor = g.ImpulseCursor,
            Deck = g.Deck.ToList(),
            Discard = g.Discard.ToList(),
            Impulse = g.Impulse.ToList(),
        };
        foreach (var sp in g.ShipPlacements)
        {
            snap.Ships.Add(new ShipDto
            {
                Owner = sp.Owner.Value,
                Kind = sp.Location is ShipLocation.OnNode ? "node" : "gate",
                Id = sp.Location switch
                {
                    ShipLocation.OnNode n => n.Node.Value,
                    ShipLocation.OnGate gateLoc => gateLoc.Gate.Value,
                    _ => 0,
                },
            });
        }
        foreach (var (nodeId, state) in g.NodeCards)
        {
            var dto = new NodeCardDto { NodeId = nodeId.Value };
            switch (state)
            {
                case NodeCardState.SectorCore: dto.State = "core"; break;
                case NodeCardState.FaceDown fd: dto.State = "down"; dto.CardId = fd.CardId; break;
                case NodeCardState.FaceUp fu: dto.State = "up"; dto.CardId = fu.CardId; break;
            }
            snap.NodeCards.Add(dto);
        }
        foreach (var p in g.Players)
        {
            snap.Players.Add(new PlayerDto
            {
                Id = p.Id.Value,
                RaceId = p.Race.Id,
                Prestige = p.Prestige,
                ShipsAvailable = p.ShipsAvailable,
                Hand = p.Hand.ToList(),
                Plan = p.Plan.ToList(),
                NextPlan = p.NextPlan?.ToList(),
                Minerals = p.Minerals.ToList(),
                Left = TechToDto(p.Techs.Left),
                Right = TechToDto(p.Techs.Right),
            });
        }
        return snap;
    }

    private static TechDto TechToDto(Tech t) => t switch
    {
        Tech.Researched r => new TechDto { Kind = "researched", CardId = r.CardId },
        Tech.BasicCommon => new TechDto { Kind = "basic_common" },
        Tech.BasicUnique bu => new TechDto { Kind = "basic_unique", RaceId = bu.Race.Id },
        _ => new TechDto { Kind = "basic_common" },
    };

    private static Tech DtoToTech(TechDto dto) => dto.Kind switch
    {
        "researched" => new Tech.Researched(dto.CardId ?? 0),
        "basic_unique" => new Tech.BasicUnique(Races.All.First(r => r.Id == (dto.RaceId ?? 1))),
        _ => Tech.BasicCommon.Instance,
    };

    // Compact one-line codec used by the GameRunner to log state at the
    // start of every turn: gzip-compressed compact JSON, base64-encoded.
    // Typical size: ~500 bytes early-game, ~1.5 KB late-game.
    public string EncodeToString()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static GameSnapshot DecodeFromString(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        using var ms = new MemoryStream(bytes);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<GameSnapshot>(json, JsonOpts)
            ?? throw new InvalidOperationException("snapshot deserialize returned null");
    }

    public string Save(string path)
    {
        File.WriteAllText(path, EncodeToString());
        return path;
    }

    public static GameSnapshot Load(string path) =>
        DecodeFromString(File.ReadAllText(path).Trim());

    // Apply a loaded snapshot onto a freshly-built GameState (built via
    // SetupFactory.NewGame using snapshot.Seed and snapshot.PlayerCount —
    // so Map, CardsById, and per-seat race assignments already match).
    public void RestoreInto(GameState g)
    {
        // Sanity check: the freshly-built game must already have each
        // saved player's seat with the saved race. This validates that
        // the seed reproduces the same race-shuffle outcome — if it
        // diverges (e.g. RNG behavior changed between versions) we fail
        // loud rather than carry inconsistent state.
        foreach (var pdto in Players)
        {
            var p = g.Players.FirstOrDefault(x => x.Id.Value == pdto.Id);
            if (p is null)
                throw new InvalidOperationException(
                    $"snapshot has player P{pdto.Id} but the rebuilt game does not");
            if (p.Race.Id != pdto.RaceId)
                throw new InvalidOperationException(
                    $"snapshot/rebuilt-game race mismatch for P{pdto.Id}: " +
                    $"snapshot race={pdto.RaceId}, rebuilt race={p.Race.Id}. " +
                    $"This usually means the seed-driven race-shuffle is no longer " +
                    $"deterministic between versions.");
        }

        g.Deck.Clear(); g.Deck.AddRange(Deck);
        g.Discard.Clear(); g.Discard.AddRange(Discard);
        g.Impulse.Clear(); g.Impulse.AddRange(Impulse);
        g.ImpulseCursor = ImpulseCursor;
        g.CurrentTurn = CurrentTurn;
        g.ActivePlayer = new PlayerId(ActivePlayer);
        g.Phase = Enum.Parse<GamePhase>(Phase);
        g.IsGameOver = IsGameOver;
        g.HomePicksDone = HomePicksDone;

        g.ShipPlacements.Clear();
        foreach (var s in Ships)
        {
            ShipLocation loc = s.Kind == "gate"
                ? new ShipLocation.OnGate(new GateId(s.Id))
                : new ShipLocation.OnNode(new NodeId(s.Id));
            g.ShipPlacements.Add(new ShipPlacement(new PlayerId(s.Owner), loc));
        }

        g.NodeCards.Clear();
        foreach (var nc in NodeCards)
        {
            NodeCardState state = nc.State switch
            {
                "down" => new NodeCardState.FaceDown(nc.CardId ?? 0),
                "up" => new NodeCardState.FaceUp(nc.CardId ?? 0),
                _ => NodeCardState.SectorCore.Instance,
            };
            g.NodeCards[new NodeId(nc.NodeId)] = state;
        }

        foreach (var pdto in Players)
        {
            var p = g.Player(new PlayerId(pdto.Id));
            p.Prestige = pdto.Prestige;
            p.ShipsAvailable = pdto.ShipsAvailable;
            p.Hand.Clear(); p.Hand.AddRange(pdto.Hand);
            p.Plan.Clear(); p.Plan.AddRange(pdto.Plan);
            p.NextPlan = pdto.NextPlan?.ToList();
            p.Minerals.Clear(); p.Minerals.AddRange(pdto.Minerals);
            p.Techs = new TechSlots(DtoToTech(pdto.Left), DtoToTech(pdto.Right));
        }
    }
}
