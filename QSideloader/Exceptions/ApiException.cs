using System;

namespace QSideloader.Exceptions;

public class ApiException(string message, Exception inner) : Exception(message, inner);