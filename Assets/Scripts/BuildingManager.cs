using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Главный по постройкам и ресурсам мира. ВСЕ изменения идут через него:
// поставить, разобрать, взять в руки, урон, удар по дереву/камню.
//
// Сеть: игрок просит -> хост делает -> хост рассылает всем, и у всех происходит то же самое.
// У каждой детали и каждого дерева/камня есть номер (netId), одинаковый у всех игроков.
// Кто зашёл позже — получает от хоста список изменений.
// Ещё хост проверяет опору: если деталь ни через что не связана с землёй (скалой, плато) —
// она по-настоящему падает и разбивается.
public class BuildingManager : MonoBehaviour
{
    public static BuildingManager Instance { get; private set; }

    public List<BuildPiece> catalog = new List<BuildPiece>(); // все виды деталей

    const ulong NoOne = Net.NoOne;
    const string MsgPlaceReq = "E.placeReq", MsgDismReq = "E.dismReq", MsgPickReq = "E.pickReq", MsgNodeReq = "E.nodeReq";
    const string MsgPlace = "E.place", MsgRemove = "E.remove", MsgGive = "E.give", MsgNodeHit = "E.nodeHit", MsgSync = "E.sync", MsgFall = "E.fall", MsgHp = "E.hp", MsgRepairReq = "E.repReq";

    int nextId = 100000; // номера для новых деталей (детали сцены — от 1)
    bool stabilityDirty;
    CustomMessagingManager registered;
    bool connectSubscribed;

    // Что изменилось в мире — для тех, кто зайдёт позже
    readonly HashSet<int> removedSceneIds = new HashSet<int>();
    readonly Dictionary<int, Placed> placed = new Dictionary<int, Placed>();
    readonly HashSet<int> depletedNodes = new HashSet<int>();
    struct Placed { public int index; public Vector3 pos; public Quaternion rot; }

    void Awake() { Instance = this; }

    // Подстраховка для старой сцены: если список деталей пуст или у деталей нет номеров — чиним сами
    void Start()
    {
        if (catalog.Count == 0)
        {
            PlayerBuilder pb = FindFirstObjectByType<PlayerBuilder>(FindObjectsInactive.Include);
            if (pb != null) catalog.AddRange(pb.pieces);
        }
        var noId = new List<BuildPiece>();
        foreach (BuildPiece p in FindObjectsByType<BuildPiece>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (p.netId == 0) noId.Add(p);
        noId.Sort((a, b) =>
        {
            Vector3 x = a.transform.position, y = b.transform.position;
            int c = x.x.CompareTo(y.x);
            if (c == 0) c = x.z.CompareTo(y.z);
            if (c == 0) c = x.y.CompareTo(y.y);
            return c;
        });
        for (int i = 0; i < noId.Count; i++) noId[i].AssignId(50000 + i);
    }

    public int IndexOf(BuildPiece piece) => piece == null ? -1 : catalog.FindIndex(p => p != null && p.pieceId == piece.pieceId);

    // ================= Запросы от игрока =================
    public static void RequestPlace(BuildPiece prefab, Vector3 pos, Quaternion rot)
    {
        BuildingManager m = Instance;
        if (m == null) return;
        int idx = m.IndexOf(prefab);
        if (idx < 0) return;
        if (Net.IsHost) { m.ServerPlace(idx, pos, rot); return; }
        FastBufferWriter w = Net.Writer();
        try { w.WriteValueSafe(idx); w.WriteValueSafe(pos); w.WriteValueSafe(rot); Net.ToHost(MsgPlaceReq, w); }
        finally { w.Dispose(); }
    }

    static bool IsLocked(int id) => BuildPiece.ById.TryGetValue(id, out BuildPiece p) && p.locked;

    public static void RequestDismantle(BuildPiece piece) => SendIdRequest(piece == null ? 0 : piece.netId, MsgDismReq, id => Instance.ServerRemove(id, true, Net.LocalId));
    public static void RequestPickUp(BuildPiece piece) => SendIdRequest(piece == null ? 0 : piece.netId, MsgPickReq, id => Instance.ServerPickUp(id, Net.LocalId));
    public static void RequestHitNode(ResourceNode node) => SendIdRequest(node == null ? 0 : node.netId, MsgNodeReq, id => Instance.ServerHitNode(id, Net.LocalId));

    static void SendIdRequest(int id, string msg, System.Action<int> asHost)
    {
        if (Instance == null || id == 0) return;
        if (msg != MsgNodeReq && IsLocked(id)) return; // дома деревни молотом не трогаем
        if (Net.IsHost) { asHost(id); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(id); Net.ToHost(msg, w); }
        finally { w.Dispose(); }
    }

    // Урон — только на хосте (его наносят твари)
    public static void Damage(BuildPiece piece, float amount)
    {
        BuildingManager m = Instance;
        if (m == null || piece == null || !Net.IsHost) return;
        piece.ApplyDamage(amount);
        if (piece.Health <= 0f) m.ServerRemove(piece.netId, true, NoOne);
        else m.SendHealth(piece);
    }

    // Починка: +25% прочности. Ресурс игрок уже потратил у себя
    public static void RequestRepair(BuildPiece piece)
    {
        if (Instance == null || piece == null) return;
        if (Net.IsHost) { Instance.ServerRepair(piece.netId); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(piece.netId); Net.ToHost(MsgRepairReq, w); }
        finally { w.Dispose(); }
    }

    void ServerRepair(int id)
    {
        if (!BuildPiece.ById.TryGetValue(id, out BuildPiece p)) return;
        p.SetHealth(p.Health + p.maxHealth * 0.25f);
        SendHealth(p);
    }

    void SendHealth(BuildPiece p)
    {
        if (!Net.Online) return;
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(p.netId); w.WriteValueSafe(p.Health); Net.ToAllClients(MsgHp, w); }
        finally { w.Dispose(); }
    }

    // ================= Подписка на сообщения =================
    void Update()
    {
        Net.EnsureRegistered(ref registered, Register);
    }

    void Register(CustomMessagingManager cmm)
    {
        // Хосту от игроков
        cmm.RegisterNamedMessageHandler(MsgPlaceReq, (sender, r) =>
        {
            r.ReadValueSafe(out int idx); r.ReadValueSafe(out Vector3 pos); r.ReadValueSafe(out Quaternion rot);
            if (Net.IsHost) ServerPlace(idx, pos, rot);
        });
        cmm.RegisterNamedMessageHandler(MsgDismReq, (sender, r) => { r.ReadValueSafe(out int id); if (Net.IsHost && !IsLocked(id)) ServerRemove(id, true, sender); });
        cmm.RegisterNamedMessageHandler(MsgPickReq, (sender, r) => { r.ReadValueSafe(out int id); if (Net.IsHost && !IsLocked(id)) ServerPickUp(id, sender); });
        cmm.RegisterNamedMessageHandler(MsgNodeReq, (sender, r) => { r.ReadValueSafe(out int id); if (Net.IsHost) ServerHitNode(id, sender); });

        // Игрокам от хоста
        cmm.RegisterNamedMessageHandler(MsgPlace, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out int idx); r.ReadValueSafe(out Vector3 pos); r.ReadValueSafe(out Quaternion rot);
            if (!Net.IsHost) LocalPlace(id, idx, pos, rot);
        });
        cmm.RegisterNamedMessageHandler(MsgRemove, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out bool debris); r.ReadValueSafe(out ulong dropFor);
            if (!Net.IsHost) LocalRemove(id, debris, dropFor);
        });
        cmm.RegisterNamedMessageHandler(MsgHp, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out float hp);
            if (!Net.IsHost && BuildPiece.ById.TryGetValue(id, out BuildPiece p)) p.SetHealth(hp);
        });
        cmm.RegisterNamedMessageHandler(MsgRepairReq, (sender, r) => { r.ReadValueSafe(out int id); if (Net.IsHost) ServerRepair(id); });
        cmm.RegisterNamedMessageHandler(MsgFall, (sender, r) => { r.ReadValueSafe(out int id); if (!Net.IsHost) LocalFall(id); });
        cmm.RegisterNamedMessageHandler(MsgGive, (sender, r) => { r.ReadValueSafe(out int idx); LocalGivePiece(idx); });
        cmm.RegisterNamedMessageHandler(MsgNodeHit, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out int left); r.ReadValueSafe(out ulong by);
            if (!Net.IsHost) LocalHitNode(id, left, by);
        });
        cmm.RegisterNamedMessageHandler(MsgSync, (sender, r) =>
        {
            r.ReadValueSafe(out int[] removed); r.ReadValueSafe(out int[] ids); r.ReadValueSafe(out int[] idxs);
            r.ReadValueSafe(out Vector3[] poss); r.ReadValueSafe(out Quaternion[] rots); r.ReadValueSafe(out int[] nodes);
            if (Net.IsHost) return;
            foreach (int id in nodes)
                if (ResourceNode.ById.TryGetValue(id, out ResourceNode n)) n.RemoveInstant();
            foreach (int id in removed) LocalRemove(id, false, NoOne);
            for (int i = 0; i < ids.Length; i++) LocalPlace(ids[i], idxs[i], poss[i], rots[i]);
        });

        if (!connectSubscribed)
        {
            connectSubscribed = true;
            Net.NM.OnClientConnectedCallback += OnClientConnected;
        }
    }

    // ================= Хост делает и рассылает =================
    void ServerPlace(int idx, Vector3 pos, Quaternion rot)
    {
        if (idx < 0 || idx >= catalog.Count) return;
        int id = nextId++;
        placed[id] = new Placed { index = idx, pos = pos, rot = rot };
        LocalPlace(id, idx, pos, rot);
        FastBufferWriter w = Net.Writer();
        try { w.WriteValueSafe(id); w.WriteValueSafe(idx); w.WriteValueSafe(pos); w.WriteValueSafe(rot); Net.ToAllClients(MsgPlace, w); }
        finally { w.Dispose(); }
    }

    // dropFor — кому выпадут ресурсы (тому, кто разобрал)
    void ServerRemove(int id, bool debris, ulong dropFor)
    {
        if (!BuildPiece.ById.ContainsKey(id)) return;
        if (!placed.Remove(id)) removedSceneIds.Add(id);
        LocalRemove(id, debris, dropFor);
        FastBufferWriter w = Net.Writer(32);
        try { w.WriteValueSafe(id); w.WriteValueSafe(debris); w.WriteValueSafe(dropFor); Net.ToAllClients(MsgRemove, w); }
        finally { w.Dispose(); }
        stabilityDirty = true;
    }

    // Деталь без опоры падает (у всех одинаково начинает падать, дальше — физика)
    void ServerFall(int id)
    {
        if (!BuildPiece.ById.ContainsKey(id)) return;
        if (!placed.Remove(id)) removedSceneIds.Add(id);
        LocalFall(id);
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(id); Net.ToAllClients(MsgFall, w); }
        finally { w.Dispose(); }
        stabilityDirty = true;
    }

    void ServerPickUp(int id, ulong forClient)
    {
        if (!BuildPiece.ById.TryGetValue(id, out BuildPiece piece)) return;
        int idx = IndexOf(piece);
        ServerRemove(id, false, NoOne);
        if (idx < 0) return;
        if (forClient == Net.LocalId) { LocalGivePiece(idx); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(idx); Net.ToClient(MsgGive, forClient, w); }
        finally { w.Dispose(); }
    }

    void ServerHitNode(int id, ulong by)
    {
        if (!ResourceNode.ById.TryGetValue(id, out ResourceNode n) || n.HitsLeft <= 0) return;
        int left = n.HitsLeft - 1;
        if (left <= 0) depletedNodes.Add(id);
        LocalHitNode(id, left, by);
        FastBufferWriter w = Net.Writer(32);
        try { w.WriteValueSafe(id); w.WriteValueSafe(left); w.WriteValueSafe(by); Net.ToAllClients(MsgNodeHit, w); }
        finally { w.Dispose(); }
    }

    void OnClientConnected(ulong clientId)
    {
        if (!Net.Online || !Net.NM.IsServer || clientId == Net.NM.LocalClientId) return;
        var ids = new List<int>(); var idxs = new List<int>(); var poss = new List<Vector3>(); var rots = new List<Quaternion>();
        foreach (var kv in placed) { ids.Add(kv.Key); idxs.Add(kv.Value.index); poss.Add(kv.Value.pos); rots.Add(kv.Value.rot); }
        FastBufferWriter w = Net.Writer(4096);
        try
        {
            w.WriteValueSafe(new List<int>(removedSceneIds).ToArray());
            w.WriteValueSafe(ids.ToArray()); w.WriteValueSafe(idxs.ToArray());
            w.WriteValueSafe(poss.ToArray()); w.WriteValueSafe(rots.ToArray());
            w.WriteValueSafe(new List<int>(depletedNodes).ToArray());
            Net.ToClient(MsgSync, clientId, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
        finally { w.Dispose(); }
    }

    // ================= То, что происходит у каждого игрока =================
    void LocalPlace(int id, int idx, Vector3 pos, Quaternion rot)
    {
        if (BuildPiece.ById.ContainsKey(id) || idx < 0 || idx >= catalog.Count) return;
        BuildPiece piece = Instantiate(catalog[idx], pos, rot, transform);
        piece.name = catalog[idx].name;
        piece.AssignId(id);
    }

    void LocalRemove(int id, bool debris, ulong dropFor)
    {
        if (!BuildPiece.ById.TryGetValue(id, out BuildPiece piece)) return;
        if (debris && dropFor != NoOne && dropFor == Net.LocalId)
        {
            for (int i = 0; i < piece.costTypes.Length && i < piece.costAmounts.Length; i++)
                ResourcePickup.Spawn(piece.costTypes[i], piece.costAmounts[i], piece.Center);
        }
        piece.Remove(debris);
    }

    void LocalFall(int id)
    {
        if (BuildPiece.ById.TryGetValue(id, out BuildPiece piece)) piece.Fall();
    }

    void LocalGivePiece(int idx)
    {
        if (idx >= 0 && idx < catalog.Count && PlayerBuilder.Local != null) PlayerBuilder.Local.ReceivePiece(catalog[idx]);
    }

    void LocalHitNode(int id, int left, ulong by)
    {
        if (!ResourceNode.ById.TryGetValue(id, out ResourceNode n)) return;
        Vector3 p = n.DropPoint;
        ResourceType type = n.resource;
        int amount = n.yieldPerHit + (left <= 0 ? n.bonusOnBreak : 0);
        n.ApplyHit(left);
        if (by == Net.LocalId) ResourcePickup.Spawn(type, amount, p); // ресурсы выпадают тому, кто бил
    }

    // ================= Опора (считает только хост) =================
    void LateUpdate()
    {
        if (!stabilityDirty || !Net.IsHost) return;
        stabilityDirty = false;
        CheckStability();
    }

    void CheckStability()
    {
        Physics.SyncTransforms();
        var supported = new HashSet<BuildPiece>();
        var queue = new Queue<BuildPiece>();
        foreach (BuildPiece p in BuildPiece.All)
            if (p.IsGrounded()) { supported.Add(p); queue.Enqueue(p); }
        while (queue.Count > 0)
            foreach (BuildPiece n in queue.Dequeue().GetNeighbours())
                if (supported.Add(n)) queue.Enqueue(n);
        var falling = new List<int>();
        foreach (BuildPiece p in BuildPiece.All)
            if (!supported.Contains(p)) falling.Add(p.netId);
        // Без опоры — падает и разбивается (ресурсы не возвращаются) — стройте с опорой!
        foreach (int id in falling) ServerFall(id);
    }

    // Если сцена старая (нет меню и сети) — пишем крупно, что делать
    void OnGUI()
    {
        if (NetworkManager.Singleton != null) return;
        GUI.Label(new Rect(20, Screen.height / 2f - 60, Screen.width - 40, 120),
            "СЦЕНА СТАРАЯ!\nВыйди из Play и нажми в меню сверху: Eirion → Создать сцену форта → Да", Hud.CenterBig);
    }
}
