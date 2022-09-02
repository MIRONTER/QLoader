using System;

namespace QSideloader.Models;

public class TaskId
{
    private readonly int _value;
    public TaskId()
    {
        var random = new Random();
        _value = random.Next(1, 16777215);
    }

    public override string ToString()
    {
        return _value.ToString("X6");
    }

    public override bool Equals(object? obj)
    {
        if (obj is TaskId id)
        {
            return _value == id._value;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return _value;
    }
}