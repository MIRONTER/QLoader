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
        return obj is TaskId id && value == id.value;
    }

    public override int GetHashCode()
    {
        return value;
    }
}