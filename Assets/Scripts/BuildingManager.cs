using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Главный по постройкам. ВСЕ изменения идут через него: поставить, разобрать, взять в руки, урон.
//
// Как это работает в сетевой игре:
//   игрок просит  ->  хост проверяет и делает  ->  хост рассылает всем, и у всех происходит то же самое.
// У каждой детали есть номер (netId), одинаковый у всех игроков, — по нему понимаем, о какой детали речь.
// Кто зашёл позже — получает от хоста список: какие детали уже снесли и какие поставили.
// Ещё хост проверяет опору: если деталь ни через что не связана с землёй — она рушится.
public class BuildingManager : NetworkBehaviour
{
    public static BuildingManager Instance { get; private set; }

    public List<BuildPiece> catalog = new List<BuildPiece>(); // все виды деталей
    [Tooltip("Деталь стоит на земле, если её низ ниже этой высоты")]
    public float groundHeight = 0.2f;

    const ulong NoOne = ulong.MaxValue;

    int nextId = 100000; // номера для новых деталей (детали сцены — от 1)
    bool stabilityDirty;

    // Что изменилось в мире — для тех, кто зайдёт позже
    readonly HashSet<int> removedSceneIds = new HashSet<int>();
    readonly Dictionary<int, Placed> placed = new Dictionary<int, Placed>();
    readonly HashSet<int> depletedNodes = new HashSet<int>(); // вырубленные деревья и разбитые камни
    struct Placed { public int index; public Vector3 pos; public Quaternion rot; }

    void Awake() { Instance = this; }

    bool Online => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
    bool Authority => !Online || IsServer; // хост (или игра без сети)
    ulong LocalId => Online ? NetworkManager.Singleton.LocalClientId : 0;

    public int IndexOf(BuildPiece piece) => piece == null ? -1 : catalog.FindIndex(p => p.pieceId == piece.pieceId);

    // ================= Запросы от игрока =================
    public static void RequestPlace(BuildPiece prefab, Vector3 pos, Quaternion rot)
    {
        BuildingManager m = Instance;
        if (m == null) return;
        int idx = m.IndexOf(prefab);
        if (idx < 0) return;
        if (m.Authority) m.ServerPlace(idx, pos, rot);
        else m.PlaceServerRpc(idx, pos, rot);
    }

    public static void RequestDismantle(BuildPiece piece)
    {
        BuildingManager m = Instance;
        if (m == null || piece == null) return;
        if (m.Authority) m.ServerRemove(piece.netId, true, m.LocalId);
        else m.DismantleServerRpc(piece.netId);
    }

    public static void RequestPickUp(BuildPiece piece)
    {
        BuildingManager m = Instance;
        if (m == null || piece == null) return;
        if (m.Authority) m.ServerPickUp(piece.netId, m.LocalId);
        else m.PickUpServerRpc(piece.netId);
    }

    // Удар по дереву / камню
    public static void RequestHitNode(ResourceNode node)
    {
        BuildingManager m = Instance;
        if (m == null || node == null) return;
        if (m.Authority) m.ServerHitNode(node.netId, m.LocalId);
        else m.HitNodeServerRpc(node.netId);
    }

    // Урон — только на хосте (его будут наносить враги)
    public static void Damage(BuildPiece piece, float amount)
    {
        BuildingManager m = Instance;
        if (m == null || piece == null || !m.Authority) return;
        piece.ApplyDamage(amount);
        if (piece.Health <= 0f) m.ServerRemove(piece.netId, true, NoOne);
    }

    // ================= Игрок -> хост =================
    [ServerRpc(RequireOwnership = false)]
    void PlaceServerRpc(int idx, Vector3 pos, Quaternion rot) => ServerPlace(idx, pos, rot);

    [ServerRpc(RequireOwnership = false)]
    void DismantleServerRpc(int id, ServerRpcParams rpc = default) => ServerRemove(id, true, rpc.Receive.SenderClientId);

    [ServerRpc(RequireOwnership = false)]
    void PickUpServerRpc(int id, ServerRpcParams rpc = default) => ServerPickUp(id, rpc.Receive.SenderClientId);

    [ServerRpc(RequireOwnership = false)]
    void HitNodeServerRpc(int id, ServerRpcParams rpc = default) => ServerHitNode(id, rpc.Receive.SenderClientId);

    // ================= Хост делает и рассылает =================
    void ServerHitNode(int id, ulong by)
    {
        if (!ResourceNode.ById.TryGetValue(id, out ResourceNode n) || n.HitsLeft <= 0) return;
        int left = n.HitsLeft - 1;
        if (left <= 0) depletedNodes.Add(id);
        LocalHitNode(id, left, by);
        if (Online) HitNodeClientRpc(id, left, by);
    }

    [ClientRpc]
    void HitNodeClientRpc(int id, int left, ulong by)
    {
        if (IsServer) return;
        LocalHitNode(id, left, by);
    }

    void LocalHitNode(int id, int left, ulong by)
    {
        if (!ResourceNode.ById.TryGetValue(id, out ResourceNode n)) return;
        Vector3 p = n.DropPoint;
        ResourceType type = n.resource;
        int amount = n.yieldPerHit + (left <= 0 ? n.bonusOnBreak : 0);
        n.ApplyHit(left);
        if (by == LocalId) ResourcePickup.Spawn(type, amount, p); // ресурсы выпадают тому, кто бил
    }

    void ServerPlace(int idx, Vector3 pos, Quaternion rot)
    {
        if (idx < 0 || idx >= catalog.Count) return;
        int id = nextId++;
        placed[id] = new Placed { index = idx, pos = pos, rot = rot };
        LocalPlace(id, idx, pos, rot);
        if (Online) PlaceClientRpc(id, idx, pos, rot);
    }

    // dropFor — кому выпадут ресурсы (тому, кто разобрал)
    void ServerRemove(int id, bool debris, ulong dropFor)
    {
        if (!BuildPiece.ById.ContainsKey(id)) return;
        if (!placed.Remove(id)) removedSceneIds.Add(id);
        LocalRemove(id, debris, dropFor);
        if (Online) RemoveClientRpc(id, debris, dropFor);
        stabilityDirty = true;
    }

    void ServerPickUp(int id, ulong forClient)
    {
        if (!BuildPiece.ById.TryGetValue(id, out BuildPiece piece)) return;
        int idx = IndexOf(piece);
        ServerRemove(id, false, NoOne);
        if (idx < 0) return;
        if (forClient == LocalId) LocalGivePiece(idx);
        else GivePieceClientRpc(idx, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { forClient } } });
    }

    [ClientRpc]
    void PlaceClientRpc(int id, int idx, Vector3 pos, Quaternion rot)
    {
        if (IsServer) return; // у хоста уже сделано
        LocalPlace(id, idx, pos, rot);
    }

    [ClientRpc]
    void RemoveClientRpc(int id, bool debris, ulong dropFor)
    {
        if (IsServer) return;
        LocalRemove(id, debris, dropFor);
    }

    [ClientRpc]
    void GivePieceClientRpc(int idx, ClientRpcParams rpc = default) => LocalGivePiece(idx);

    // ================= То, что происходит у каждого игрока =================
    void LocalPlace(int id, int idx, Vector3 pos, Quaternion rot)
    {
        if (BuildPiece.ById.ContainsKey(id)) return;
        BuildPiece piece = Instantiate(catalog[idx], pos, rot, transform);
        piece.name = catalog[idx].name;
        piece.AssignId(id);
    }

    void LocalRemove(int id, bool debris, ulong dropFor)
    {
        if (!BuildPiece.ById.TryGetValue(id, out BuildPiece piece)) return;
        if (debris && dropFor != NoOne && dropFor == LocalId)
        {
            ResourcePickup.Spawn(ResourceType.Wood, piece.costWood, piece.Center);
            ResourcePickup.Spawn(ResourceType.Stone, piece.costStone, piece.Center);
        }
        piece.Remove(debris);
    }

    void LocalGivePiece(int idx)
    {
        if (PlayerBuilder.Local != null) PlayerBuilder.Local.ReceivePiece(catalog[idx]);
    }

    // ================= Тем, кто зашёл позже =================
    public override void OnNetworkSpawn()
    {
        if (IsServer) NetworkManager.OnClientConnectedCallback += OnClientConnected;
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager != null) NetworkManager.OnClientConnectedCallback -= OnClientConnected;
    }

    void OnClientConnected(ulong clientId)
    {
        if (clientId == NetworkManager.ServerClientId) return;
        var removed = new List<int>(removedSceneIds);
        var ids = new List<int>();
        var idxs = new List<int>();
        var poss = new List<Vector3>();
        var rots = new List<Quaternion>();
        foreach (var kv in placed)
        {
            ids.Add(kv.Key); idxs.Add(kv.Value.index); poss.Add(kv.Value.pos); rots.Add(kv.Value.rot);
        }
        var nodes = new List<int>(depletedNodes);
        SyncStateClientRpc(removed.ToArray(), ids.ToArray(), idxs.ToArray(), poss.ToArray(), rots.ToArray(), nodes.ToArray(),
            new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } });
    }

    [ClientRpc]
    void SyncStateClientRpc(int[] removed, int[] ids, int[] idxs, Vector3[] poss, Quaternion[] rots, int[] nodes, ClientRpcParams rpc = default)
    {
        if (IsServer) return;
        foreach (int id in nodes)
            if (ResourceNode.ById.TryGetValue(id, out ResourceNode n)) n.RemoveInstant();
        foreach (int id in removed) LocalRemove(id, false, NoOne);
        for (int i = 0; i < ids.Length; i++) LocalPlace(ids[i], idxs[i], poss[i], rots[i]);
    }

    // ================= Опора (считает только хост) =================
    void LateUpdate()
    {
        if (!stabilityDirty || !Authority) return;
        stabilityDirty = false;
        CheckStability();
    }

    void CheckStability()
    {
        Physics.SyncTransforms();
        var supported = new HashSet<BuildPiece>();
        var queue = new Queue<BuildPiece>();

        foreach (BuildPiece p in BuildPiece.All)
            if (p.transform.position.y < groundHeight) { supported.Add(p); queue.Enqueue(p); }

        while (queue.Count > 0)
            foreach (BuildPiece n in queue.Dequeue().GetNeighbours())
                if (supported.Add(n)) queue.Enqueue(n);

        var falling = new List<int>();
        foreach (BuildPiece p in BuildPiece.All)
            if (!supported.Contains(p)) falling.Add(p.netId);

        // Рухнувшее просто рассыпается (без ресурсов) — стройте с опорой!
        foreach (int id in falling) ServerRemove(id, true, NoOne);
    }
}
