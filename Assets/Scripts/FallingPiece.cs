using UnityEngine;

// Падающая деталь: разбивается при ударе о землю (или через 4 сек)
public class FallingPiece : MonoBehaviour
{
    public BuildPiece piece;
    float born;

    void Start() { born = Time.time; }

    void Update()
    {
        if (Time.time - born > 4f || transform.position.y < -50f) Smash();
    }

    void OnCollisionEnter(Collision c)
    {
        if (Time.time - born > 0.3f && c.relativeVelocity.magnitude > 3f) Smash();
    }

    void Smash()
    {
        if (piece != null) piece.SpawnDebris();
        Destroy(gameObject);
    }
}
