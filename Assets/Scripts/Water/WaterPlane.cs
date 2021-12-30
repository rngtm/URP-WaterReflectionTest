using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class WaterPlane : MonoBehaviour
{
    public static WaterPlane Instance { get; private set; } 
    public float WaterY => transform.position.y;

    private void Update()
    {
        Instance = this;
    }
}
