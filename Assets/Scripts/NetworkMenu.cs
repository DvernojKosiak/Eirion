using System.Net;
using System.Net.Sockets;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

// Главное меню (пока простое):
//   «Играть одному»            — мир только для тебя
//   «Создать мир для друзей»   — друзья смогут подключиться по твоему адресу
//   «Подключиться»             — зайти в мир друга по его адресу
public class NetworkMenu : MonoBehaviour
{
    public ushort port = 7777;

    string address = "127.0.0.1";
    string status = "";
    string myAddresses = "";
    bool hostingForFriends;

    NetworkManager Net => NetworkManager.Singleton;

    void Start()
    {
        myAddresses = LocalAddresses();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (Net != null) Net.OnClientDisconnectCallback += OnDisconnected;
    }

    void OnDestroy()
    {
        if (Net != null) Net.OnClientDisconnectCallback -= OnDisconnected;
    }

    void OnDisconnected(ulong clientId)
    {
        if (Net == null || Net.IsServer) return;
        status = "Не удалось подключиться или связь пропала.\nПерезапусти игру (▶ два раза) и попробуй снова.";
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    UnityTransport Transport => Net != null ? Net.GetComponent<UnityTransport>() : null;

    void PlaySolo()
    {
        Transport.SetConnectionData("127.0.0.1", port, "127.0.0.1");
        hostingForFriends = false;
        if (!Net.StartHost()) status = "Не получилось запустить мир.";
    }

    void HostForFriends()
    {
        Transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
        hostingForFriends = true;
        if (!Net.StartHost()) status = "Не получилось создать мир. Может, порт " + port + " занят?";
    }

    void Join()
    {
        Transport.SetConnectionData(address.Trim(), port);
        status = "Подключаюсь к " + address + "...";
        if (!Net.StartClient()) status = "Не получилось начать подключение.";
    }

    void OnGUI()
    {
        if (Net == null) { GUI.Label(new Rect(20, 20, 600, 30), "Нет NetworkManager в сцене", Hud.Big); return; }

        if (Net.IsListening)
        {
            // В игре: показываем адрес хоста и сколько игроков
            if (Net.IsServer && hostingForFriends)
                GUI.Label(new Rect(Screen.width - 420, 10, 410, 60),
                          "Твой адрес для друзей: " + myAddresses + "\nИгроков: " + Net.ConnectedClientsIds.Count, Hud.Small);
            if (!Net.IsServer && !Net.IsConnectedClient)
                GUI.Label(new Rect(Screen.width / 2f - 200, Screen.height / 2f, 600, 30), status, Hud.Big);
            return;
        }

        // Меню
        float w = 380, x = Screen.width / 2f - w / 2f, y = Screen.height / 2f - 190;
        GUI.DrawTexture(new Rect(x - 20, y - 20, w + 40, 400), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, new Color(0, 0, 0, 0.7f), 0, 10);
        GUI.Label(new Rect(x, y, w, 40), "EIRION", Hud.Big);
        y += 50;
        if (GUI.Button(new Rect(x, y, w, 40), "Играть одному")) PlaySolo();
        y += 50;
        if (GUI.Button(new Rect(x, y, w, 40), "Создать мир для друзей")) HostForFriends();
        y += 60;
        GUI.Label(new Rect(x, y, w, 24), "Адрес друга:", Hud.Small);
        y += 26;
        address = GUI.TextField(new Rect(x, y, w, 30), address);
        y += 40;
        if (GUI.Button(new Rect(x, y, w, 40), "Подключиться")) Join();
        y += 50;
        GUI.Label(new Rect(x, y, w, 80), status, Hud.Small);
    }

    static string LocalAddresses()
    {
        try
        {
            string result = "";
            foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    result += (result.Length > 0 ? ", " : "") + ip;
            return result.Length > 0 ? result : "не найден";
        }
        catch { return "не найден"; }
    }
}
