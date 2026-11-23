using System.Text.Json;
using Xunit;
using Alberto.Results;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// Fixtures for EventEnvelopeExtensions tests
//
// Convention: use the "cvt-" prefix on all slugs to avoid collisions with
// the many other [EventType] declarations scattered across this test assembly.
//
// The "wrong return" pair shares a slug in the registry (the serializer maps
// the slug to WrongReturn) but TEvent is Expected — those are unrelated types,
// so the type-mismatch guard fires.
// ---------------------------------------------------------------------------

// The type the serializer will actually produce.
[EventType("cvt-wrong-return")]
file record CvtWrongReturnType(int X) : IEvent;

// The type the caller asks DeserializeEvent to produce — not assignable from CvtWrongReturnType.
[EventType("cvt-expected-type")]
file record CvtExpectedType(int X) : IEvent;

// V2 handler type used to trigger the EV-1 stale-version guard (no serializer).
[EventType("cvt-ev1-guard", Version = 2)]
file record CvtEv1GuardEvent(Guid Id, string Extra) : IEvent;

// V1 type used for the EV-1 guard — envelope carries version=1, handler expects version=2.
file record CvtEv1GuardEventV1(Guid Id);

// A type without [EventType], used to probe EventType.FromType and GetEventTypeId.
file record CvtNoAttributeEvent(int X) : IEvent;

// ---------------------------------------------------------------------------
// Result<T> — mutation targets
// ---------------------------------------------------------------------------

public sealed class ResultGenericMutationTests
{
    private static readonly Problem SomeProblem =
        Problem.Create("test.error", "Something went wrong");

    // -----------------------------------------------------------------------
    // IsFailure => !IsSuccess   (un-negate mutant)
    //
    // A mutant that removes the '!' would make IsFailure always true or always
    // false. Testing both a success and a failure result kills it.
    // -----------------------------------------------------------------------

    [Fact]
    public void IsFailure_IsFalse_WhenSuccess()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void IsFailure_IsTrue_WhenFailed()
    {
        var result = Result<int>.Fail(SomeProblem);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
    }

    // -----------------------------------------------------------------------
    // Problems => _problems ?? []   (both null-coalescing mutants)
    //
    // When _problems is null (default struct), the ?? [] branch must return an
    // empty list, not null. When there are problems, the list must come back.
    // -----------------------------------------------------------------------

    [Fact]
    public void Problems_ReturnsEmpty_WhenDefaultStruct()
    {
        // default(Result<int>) has _problems == null because the private field
        // is never initialised; the ?? [] fallback is what saves the caller.
        var result = default(Result<int>);

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Problems);
    }

    [Fact]
    public void Problems_ReturnsProblems_WhenResultFailed()
    {
        var result = Result<int>.Fail(SomeProblem);

        Assert.Equal("test.error", Assert.Single(result.Problems).Code);
    }

    [Fact]
    public void Problems_ReturnsAllProblems_WhenMultipleSupplied()
    {
        var p1 = Problem.Create("err.one", "First");
        var p2 = Problem.Create("err.two", "Second");

        var result = Result<int>.Fail(new[] { p1, p2 });

        Assert.Equal(2, result.Problems.Count());
        Assert.Contains(result.Problems, x => x.Code == "err.one");
        Assert.Contains(result.Problems, x => x.Code == "err.two");
    }

    // -----------------------------------------------------------------------
    // Fail(Problem) and Fail(IEnumerable<Problem>) boolean mutants
    //
    // If the 'false' literal were flipped to 'true', IsSuccess would be true on
    // a failed result. Assert IsSuccess is false on every Fail path.
    // -----------------------------------------------------------------------

    [Fact]
    public void Fail_SingleProblem_IsSuccess_IsFalse()
    {
        var result = Result<string>.Fail(SomeProblem);

        Assert.False(result.IsSuccess, "Fail must set IsSuccess = false; a mutant flipping false→true is caught here");
    }

    [Fact]
    public void Fail_EnumerableProblem_IsSuccess_IsFalse()
    {
        var result = Result<string>.Fail(new[] { SomeProblem });

        Assert.False(result.IsSuccess, "Fail(IEnumerable) must set IsSuccess = false; a mutant flipping false→true is caught here");
    }

    // -----------------------------------------------------------------------
    // Map ternary mutants
    //
    // Map = IsSuccess ? Success(transform(Value)) : Fail(Problems)
    //
    // Success mutant: flips to Fail(Problems) on a success input.
    //   → Assert the mapped result is a success with the transformed value.
    //   → Assert the transform function was called.
    //
    // Failure mutant: flips to Success(transform(Value)) on a failure input.
    //   → Assert the mapped result is a failure with the original problems.
    //   → Assert the transform function was NOT called.
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_OnSuccess_TransformsValueAndCallsTransform()
    {
        var result = Result<int>.Success(7);
        var transformCalled = false;

        var mapped = result.Map(n =>
        {
            transformCalled = true;
            return n * 2;
        });

        Assert.True(mapped.IsSuccess, "Map on a success must yield a success");
        Assert.Equal(14, mapped.Value);
        Assert.True(transformCalled, "the transform must be invoked on a success result");
    }

    [Fact]
    public void Map_OnFailure_PreservesProblemsAndDoesNotCallTransform()
    {
        var result = Result<int>.Fail(SomeProblem);
        var transformCalled = false;

        var mapped = result.Map(n =>
        {
            transformCalled = true;
            return n * 2;
        });

        Assert.False(mapped.IsSuccess, "Map on a failure must yield a failure");
        // the original problems must be preserved on the failure path
        Assert.Equal("test.error", Assert.Single(mapped.Problems).Code);
        Assert.False(transformCalled, "the transform must NOT be invoked when the input is a failed result; " +
            "a mutant that flips the ternary would call it and this assertion would catch that");
    }
}

// ---------------------------------------------------------------------------
// Non-generic Result — cover the same IsFailure and Problems gaps there too.
// ---------------------------------------------------------------------------

public sealed class ResultNonGenericMutationTests
{
    private static readonly Problem SomeProblem =
        Problem.Create("ng.error", "Non-generic error");

    [Fact]
    public void IsFailure_IsFalse_WhenSuccess()
    {
        var result = Result.Success();
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void IsFailure_IsTrue_WhenFailed()
    {
        var result = Result.Fail(SomeProblem);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Problems_ReturnsEmpty_WhenDefaultStruct()
    {
        var result = default(Result);
        Assert.Empty(result.Problems);
        Assert.NotNull(result.Problems);
    }

    [Fact]
    public void Problems_ReturnsProblems_WhenFailed()
    {
        var result = Result.Fail(SomeProblem);
        Assert.Equal("ng.error", Assert.Single(result.Problems).Code);
    }
}

// ---------------------------------------------------------------------------
// EventEnvelopeExtensions.DeserializeEvent — the highest-value target
//
// This internal static method is reachable because Alberto grants
// InternalsVisibleTo to Alberto.Tests (declared in Alberto.csproj).
//
// Guard (a): the serializer returned a type the handler did not expect.
// Guard (b): envelope.EventType.Version < the version declared by [EventType].
// ---------------------------------------------------------------------------

public sealed class EventEnvelopeExtensionsDeserializeTests
{
    // Shared envelope builder to keep test setup short.
    private static EventEnvelope MakeEnvelope(string slug, int version, string json) => new()
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType(slug, version),
        EventData = json,
        Tags = [],
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
        TenantId = null,
    };

    // -----------------------------------------------------------------------
    // Guard (a): serializer produces a type not assignable to TEvent
    //
    // Registry maps "cvt-wrong-return" → CvtWrongReturnType (version 1).
    // We ask DeserializeEvent for CvtExpectedType, which is completely unrelated.
    // The serializer returns a CvtWrongReturnType and the 'is not TEvent' guard fires.
    //
    // IMPORTANT: envelope.EventType.Version (1) must equal the declared version of
    // CvtWrongReturnType (also 1) so the serializer's own version-gap guard does NOT
    // fire first — we are specifically testing the type-mismatch guard here.
    // -----------------------------------------------------------------------

    [Fact]
    public void WhenSerializerReturnsWrongType_Throws()
    {
        // Registry maps the slug to CvtWrongReturnType (a type that carries [EventType("cvt-wrong-return")]).
        var serializer = EventSerializer.FromRegistry(new Dictionary<string, Type>
        {
            ["cvt-wrong-return"] = typeof(CvtWrongReturnType)
        });

        var envelope = MakeEnvelope(
            slug: "cvt-wrong-return",
            version: 1,
            json: JsonSerializer.Serialize(new CvtWrongReturnType(99)));

        // Ask for CvtExpectedType — the serializer yields CvtWrongReturnType, which is not
        // assignable to CvtExpectedType, so the guard must throw.
        var act = () => EventEnvelopeExtensions.DeserializeEvent<CvtExpectedType>(envelope, serializer);

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Matches("(?is)CvtWrongReturnType.*CvtExpectedType", exception.Message);
    }

    // -----------------------------------------------------------------------
    // Guard (b): EV-1 stale-version guard — no serializer, stored version < handler version
    //
    // CvtEv1GuardEvent declares [EventType("cvt-ev1-guard", Version = 2)].
    // The envelope carries EventType.Version = 1.
    // With serializer = null, the fallback path reads the declared version and
    // refuses to deserialize a v1 payload into a v2 handler shape.
    // -----------------------------------------------------------------------

    [Fact]
    public void WhenNoSerializerAndStoredVersionLtDeclaredVersion_Throws()
    {
        // Serialize a v1 payload (no Extra field) as the stored event data.
        var v1Json = JsonSerializer.Serialize(new CvtEv1GuardEventV1(Guid.NewGuid()));

        var envelope = MakeEnvelope(
            slug: "cvt-ev1-guard",
            version: 1,      // stored at v1
            json: v1Json);

        // serializer = null triggers the raw-JSON fallback path,
        // where the EV-1 version guard is the first check.
        var act = () => EventEnvelopeExtensions.DeserializeEvent<CvtEv1GuardEvent>(envelope, null);

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Matches("(?is)cvt-ev1-guard.*schema version 1.*expects version 2", exception.Message);
    }
}

// ---------------------------------------------------------------------------
// EventTag — equality operators, GetHashCode, implicit string conversion,
// Parse invalid format, reserved-concept constructor rejection
// ---------------------------------------------------------------------------

public sealed class EventTagMutationTests
{
    private static readonly EventTag TagA = new("order", "123");
    private static readonly EventTag TagB = new("order", "123"); // equal to A
    private static readonly EventTag TagC = new("order", "456"); // different id

    // -----------------------------------------------------------------------
    // == and != operators — both directions
    //
    // Four assertions: A==B true, A!=B false, A==C false, A!=C true.
    // A one-sided test would not kill an operator that always returns true/false.
    // -----------------------------------------------------------------------

    [Fact]
    public void EqualityOperator_ReturnsTrue_ForEqualTags()
    {
        Assert.True((TagA == TagB));
        Assert.False((TagA != TagB));
    }

    [Fact]
    public void EqualityOperator_ReturnsFalse_ForDifferentTags()
    {
        Assert.False((TagA == TagC));
        Assert.True((TagA != TagC));
    }

    // -----------------------------------------------------------------------
    // GetHashCode — same value → same hash; different values → different hash
    // -----------------------------------------------------------------------

    [Fact]
    public void GetHashCode_IsSame_ForEqualTags()
    {
        Assert.Equal(TagB.GetHashCode(), TagA.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DiffersByConceptOrId()
    {
        var byDifferentConcept = new EventTag("customer", "123");
        Assert.NotEqual(byDifferentConcept.GetHashCode(), TagA.GetHashCode());
        Assert.NotEqual(TagC.GetHashCode(), TagA.GetHashCode());
    }

    // -----------------------------------------------------------------------
    // Implicit string conversion
    //
    // (string)tag must equal tag.Value. A mutant that removes the operator or
    // returns an empty string would break this.
    // -----------------------------------------------------------------------

    [Fact]
    public void ImplicitStringConversion_ReturnsValue()
    {
        string s = TagA;
        Assert.Equal("order:123", s);
        Assert.Equal(TagA.Value, s);
    }

    // -----------------------------------------------------------------------
    // Parse — invalid format (no colon) must throw ArgumentException
    // -----------------------------------------------------------------------

    [Fact]
    public void Parse_WithoutColon_ThrowsArgumentException()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => EventTag.Parse("nocolon"));
        Assert.Contains("Invalid tag format", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // (concept, id) constructor — reserved-concept rejection
    //
    // Any concept starting with "_" must be rejected. This is already covered
    // by ReservedTagGuardTests for "_version"; here we confirm the guard fires
    // for a plausible-but-hypothetical framework concept so the test does not
    // accidentally pass because the code exact-matches "_version".
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_RejectsReservedConcept()
    {
        // Use a hypothetical reserved concept — not one Alberto uses today —
        // so the test fails only if the whole prefix guard is missing, not if
        // someone removes "_version" specifically.
        var exception = Assert.ThrowsAny<ArgumentException>(() => new EventTag("_causation", "abc"));
        Assert.Equal("concept", exception.ParamName);
        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// ---------------------------------------------------------------------------
// EventType — equality operators, GetHashCode, constructor guards,
// FromType / GetEventTypeId on types without the attribute,
// implicit string conversions
// ---------------------------------------------------------------------------

public sealed class EventTypeMutationTests
{
    private static readonly EventType TypeA = new("order-placed", 1);
    private static readonly EventType TypeB = new("order-placed", 2); // same Id, different version → still equal
    private static readonly EventType TypeC = new("order-cancelled", 1); // different Id → not equal

    // -----------------------------------------------------------------------
    // == and != operators — both directions (version excluded from equality)
    // -----------------------------------------------------------------------

    [Fact]
    public void EqualityOperator_ReturnsTrue_ForSameId_DifferentVersion()
    {
        Assert.True((TypeA == TypeB));
        Assert.False((TypeA != TypeB));
    }

    [Fact]
    public void EqualityOperator_ReturnsFalse_ForDifferentId()
    {
        Assert.False((TypeA == TypeC));
        Assert.True((TypeA != TypeC));
    }

    // -----------------------------------------------------------------------
    // GetHashCode — different IDs produce different hashes (extremely likely)
    // -----------------------------------------------------------------------

    [Fact]
    public void GetHashCode_DiffersByEventTypeId()
    {
        Assert.NotEqual(TypeC.GetHashCode(), TypeA.GetHashCode());
    }

    // -----------------------------------------------------------------------
    // Constructor — version < 1 must be rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_RejectsVersionLessThanOne()
    {
        var exception = Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new EventType("order-placed", 0));
        Assert.Contains("version must be >= 1", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // FromType / GetEventTypeId — types without [EventType] must throw
    // -----------------------------------------------------------------------

    [Fact]
    public void FromType_ThrowsForTypeWithoutAttribute()
    {
        var exception = Assert.ThrowsAny<InvalidOperationException>(() => EventType.FromType(typeof(CvtNoAttributeEvent)));
        Assert.Matches("(?is)does not have an.*\\[EventType]", exception.Message);
    }

    [Fact]
    public void GetEventTypeId_ThrowsForTypeWithoutAttribute()
    {
        var exception = Assert.ThrowsAny<InvalidOperationException>(() => EventTypeAttribute.GetEventTypeId(typeof(CvtNoAttributeEvent)));
        Assert.Matches("(?is)does not have an.*\\[EventType]", exception.Message);
    }

    // -----------------------------------------------------------------------
    // Implicit conversions
    //
    // EventType → string: should return Id (not ToString(), not Version).
    // string → EventType: should produce an EventType with that Id (version=1).
    // -----------------------------------------------------------------------

    [Fact]
    public void ImplicitConversion_EventTypeToString_ReturnsId()
    {
        string s = TypeA;
        Assert.Equal("order-placed", s);
        // Make sure version is NOT included in the string — that would break query filters.
        Assert.DoesNotContain("1", s);
    }

    [Fact]
    public void ImplicitConversion_StringToEventType_ProducesEventType()
    {
        EventType fromString = "order-placed";
        Assert.Equal("order-placed", fromString.Id);
        // the string→EventType implicit conversion defaults to version 1
        Assert.Equal(1, fromString.Version);
    }
}

// ---------------------------------------------------------------------------
// DcbConflictException — constructors and ToProblem()
// ---------------------------------------------------------------------------

public sealed class DcbConflictExceptionTests
{
    private static readonly DcbQuery SomeQuery =
        DcbQuery.For("order", "42").WithTypes("order-placed");

    // -----------------------------------------------------------------------
    // (string, Exception) constructor — the "fallback" overload
    //
    // Docs state: ConflictingPosition = -1, ExpectedPosition = -1,
    //             Query = DcbQuery.Empty.
    // Assert all three so a mutant that sets them to something else is caught.
    // -----------------------------------------------------------------------

    [Fact]
    public void FallbackConstructor_SetsNegativePositionsAndEmptyQuery()
    {
        var inner = new Exception("db error");
        var ex = new DcbConflictException("conflict happened", inner);

        Assert.Equal(-1, ex.ConflictingPosition);
        Assert.Equal(-1, ex.ExpectedPosition);
        Assert.True(ex.Query.IsEmpty, "the fallback constructor must set Query = DcbQuery.Empty");
        Assert.Equal("conflict happened", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    // -----------------------------------------------------------------------
    // (string, long, long, DcbQuery, Exception) constructor — with inner exception
    // -----------------------------------------------------------------------

    [Fact]
    public void FullConstructorWithInner_SetsAllProperties()
    {
        var inner = new Exception("postgres error");
        var ex = new DcbConflictException(
            "DCB conflict",
            conflictingPosition: 99L,
            expectedPosition: 50L,
            query: SomeQuery,
            innerException: inner);

        Assert.Equal("DCB conflict", ex.Message);
        Assert.Equal(99L, ex.ConflictingPosition);
        Assert.Equal(50L, ex.ExpectedPosition);
        Assert.Equal(SomeQuery, ex.Query);
        Assert.Same(inner, ex.InnerException);
    }

    // -----------------------------------------------------------------------
    // ToProblem() — must produce a Problem with the right code and both positions
    // -----------------------------------------------------------------------

    [Fact]
    public void ToProblem_ProducesExpectedProblem()
    {
        var ex = new DcbConflictException(
            conflictingPosition: 77L,
            expectedPosition: 30L,
            query: SomeQuery);

        var problem = ex.ToProblem();

        Assert.Equal(DcbConflictException.ProblemCode, problem.Code);
        Assert.Equal(ex.Message, problem.Message);
        Assert.Equal(77L, Assert.Contains("conflictingPosition", problem.Details));
        Assert.Equal(30L, Assert.Contains("expectedPosition", problem.Details));
    }

    // -----------------------------------------------------------------------
    // ToProblem on the fallback constructor — positions are -1 in the Problem too
    // -----------------------------------------------------------------------

    [Fact]
    public void ToProblem_OnFallbackException_ReportsNegativePositions()
    {
        var ex = new DcbConflictException("oops", new Exception("inner"));
        var problem = ex.ToProblem();

        Assert.Equal(DcbConflictException.ProblemCode, problem.Code);
        Assert.Equal(-1L, problem.Details["conflictingPosition"]);
        Assert.Equal(-1L, problem.Details["expectedPosition"]);
    }
}
