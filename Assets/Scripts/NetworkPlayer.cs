using Unity.Netcode;
using UnityEngine;

// Сетевой игрок. У каждого игрока в мире есть такой объект.
// Свой игрок: включаем управление и сажаем в него камеру.
// Чужой игрок: управление выключено, он просто повторяет то, что присылает сеть.
public class NetworkPlayer : NetworkBehaviour
{
    public Behaviour[] ownerOnly;          // скрипты, которые работают только у своего игрока
    public Transform cameraPivot;          // «голова», куда сажаем камеру
    public Renderer bodyRenderer;          // тело — красим в цвет игрока
    public Vector3 spawnPoint = new Vector3(6f, 5.2f, -10f);

    // Что в руке (меч/молот) — чтобы другие видели
    readonly NetworkVariable<int> tool = new NetworkVariable<int>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    static readonly Color[] colors =
    {
        new Color(0.75f, 0.25f, 0.2f), new Color(0.2f, 0.45f, 0.8f), new Color(0.25f, 0.65f, 0.3f),
        new Color(0.85f, 0.7f, 0.2f), new Color(0.6f, 0.3f, 0.75f), new Color(0.9f, 0.5f, 0.2f),
    };

    PlayerHotbar hotbar;

    public override void OnNetworkSpawn()
    {
        hotbar = GetComponent<PlayerHotbar>();
        name = "Player_" + OwnerClientId;

        if (bodyRenderer != null)
        {
            Color c = colors[(int)(OwnerClientId % (ulong)colors.Length)];
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            bodyRenderer.SetPropertyBlock(block);
        }

        if (IsOwner)
        {
            // Ставим на точку появления (каждого игрока чуть в стороне)
            CharacterController cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            transform.position = spawnPoint + new Vector3((OwnerClientId % 4) * 2f - 3f, 0f, 0f);
            if (cc != null) cc.enabled = true;

            // Камера — в голову своего игрока
            Camera cam = Camera.main;
            if (cam != null && cameraPivot != null)
            {
                cam.transform.SetParent(cameraPivot, false);
                cam.transform.localPosition = Vector3.zero;
                cam.transform.localRotation = Quaternion.identity;
                PlayerCamera pc = cameraPivot.GetComponent<PlayerCamera>();
                if (pc != null) pc.cameraObject = cam.transform;
            }

            foreach (Behaviour b in ownerOnly)
                if (b != null) b.enabled = true;
        }
        else
        {
            tool.OnValueChanged += OnToolChanged;
            if (hotbar != null) hotbar.ShowTool((Tool)tool.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        tool.OnValueChanged -= OnToolChanged;
        if (IsOwner)
        {
            // Камеру не даём уничтожить вместе с игроком
            Camera cam = GetComponentInChildren<Camera>();
            if (cam != null) cam.transform.SetParent(null, true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void OnToolChanged(int oldValue, int newValue)
    {
        if (hotbar != null) hotbar.ShowTool((Tool)newValue);
    }

    void Update()
    {
        if (IsOwner && hotbar != null && tool.Value != (int)hotbar.Current)
            tool.Value = (int)hotbar.Current;
    }
}
