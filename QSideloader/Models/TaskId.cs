using System;

namespace QSideloader.Models;

public class TaskId
{
    private readonly int value;
    public TaskId()
    {
        var random = new Random();
        value = random.Next(1, 16777215);
    }

    public override string ToString()
    {
        return value.ToString("X6");
    }

    public override bool Equals(object? obj)
    {
        if (obj is TaskId id)
        {
            return value == id.value;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return value;
    }
}