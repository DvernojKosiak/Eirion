using UnityEngine;

// Частичка (пыль, щепка, брызги): летит, падает и исчезает. Без физики — дёшево.
public class FlyAway : MonoBehaviour
{
    public Vector3 velocity;

    void Update()
    {
        velocity += Vector3.down * 12f * Time.deltaTime;
        transform.position += velocity * Time.deltaTime;
        transform.localScale *= 1f - Time.deltaTime * 1.5f;
    }
}
