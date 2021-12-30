using UnityEngine;

public class SimpleRotation : MonoBehaviour
{
    [SerializeField] private float rotation = 45f;
    [SerializeField] private Vector3 axis = new Vector3(0, 1, 0);
    
    void Update()
    {
        transform.Rotate( axis.normalized, rotation * Time.deltaTime, Space.World);
    }
}
