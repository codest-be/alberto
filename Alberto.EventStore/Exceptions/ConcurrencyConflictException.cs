namespace Alberto.EventStore.Exceptions;

public sealed class ConcurrencyConflictException(string s) : Exception(s);