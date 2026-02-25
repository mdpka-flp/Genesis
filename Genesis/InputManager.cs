using Raylib_cs;
using System.Collections.Generic;
using System;

public class InputManager
{
    private Dictionary<KeyboardKey, Action> _keyPressedActions = new Dictionary<KeyboardKey, Action>();
    
    public void RegAction(KeyboardKey key, Action action)
    {
        _keyPressedActions[key] = action;
    }
    
    public void Update()
    {
        foreach (var kv in _keyPressedActions)
        {
            if (Raylib.IsKeyPressed(kv.Key))
            {
                kv.Value?.Invoke();
            }
        }
    }
    
    public void UnregAction(KeyboardKey key)
    {
        _keyPressedActions.Remove(key);
    }
}