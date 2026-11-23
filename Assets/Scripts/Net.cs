using System;
using Unity.Collections;
using Unity.Netcode;

// Помощник для сети. Мы не вешаем NetworkObject на менеджеры в сцене —
// вместо этого шлём простые «именные сообщения» (так надёжнее и проще).
//   Хост (или игра одному) — главный: он решает, что происходит, и рассылает всем.
public static class Net
{
    public const ulong NoOne = ulong.MaxValue;

    public static NetworkManager NM => NetworkManager.Singleton;
    public static bool Online => NM != null && NM.IsListening;
    public static bool IsHost => !Online || NM.IsServer;       // главный ли я
    public static ulong LocalId => Online ? NM.LocalClientId : 0;

    public static FastBufferWriter Writer(int size = 256) => new FastBufferWriter(size, Allocator.Temp, 1 << 16);

    // Отправить хосту
    public static void ToHost(string name, FastBufferWriter w, NetworkDelivery d = NetworkDelivery.ReliableSequenced)
    {
        if (Online) NM.CustomMessagingManager.SendNamedMessage(name, NetworkManager.ServerClientId, w, d);
    }

    // Отправить одному игроку (себе не шлём)
    public static void ToClient(string name, ulong client, FastBufferWriter w, NetworkDelivery d = NetworkDelivery.ReliableSequenced)
    {
        if (Online && client != NM.LocalClientId) NM.CustomMessagingManager.SendNamedMessage(name, client, w, d);
    }

    // Хост -> всем остальным игрокам
    public static void ToAllClients(string name, FastBufferWriter w, NetworkDelivery d = NetworkDelivery.ReliableSequenced)
    {
        if (!Online || !NM.IsServer) return;
        foreach (ulong id in NM.ConnectedClientsIds)
            if (id != NM.LocalClientId) NM.CustomMessagingManager.SendNamedMessage(name, id, w, d);
    }

    // Подписаться на сообщения, когда сеть запустилась (и заново при новом запуске)
    public static void EnsureRegistered(ref CustomMessagingManager registeredWith, Action<CustomMessagingManager> register)
    {
        if (!Online) { registeredWith = null; return; }
        CustomMessagingManager cmm = NM.CustomMessagingManager;
        if (cmm == null || cmm == registeredWith) return;
        registeredWith = cmm;
        register(cmm);
    }
}
