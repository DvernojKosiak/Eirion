using Unity.Netcode.Components;

// Передаёт положение игрока по сети.
// Обычный NetworkTransform слушается только хоста, а этот — самого игрока,
// поэтому своё движение не тормозит, даже если ты не хост.
public class OwnerNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative() => false;
}
