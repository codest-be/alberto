using System.Text.Json;
using FluentAssertions;
using Xunit;

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

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
    }

    [Fact]
    public void IsFailure_IsTrue_WhenFailed()
    {
        var result = Result<int>.Fail(SomeProblem);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
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

        result.Problems.Should().BeEmpty();
        result.Problems.Should().NotBeNull();
    }

    [Fact]
    public void Problems_ReturnsProblems_WhenResultFailed()
    {
        var result = Result<int>.Fail(SomeProblem);

        result.Problems.Should().ContainSingle()
              .Which.Code.Should().Be("test.error");
    }

    [Fact]
    public void Problems_ReturnsAllProblems_WhenMultipleSupplied()
    {
        var p1 = Problem.Create("err.one", "First");
        var p2 = Problem.Create("err.two", "Second");

        var result = Result<int>.Fail(new[] { p1, p2 });

        result.Problems.Should().HaveCount(2);
        result.Problems.Should().Contain(x => x.Code == "err.one");
        result.Problems.Should().Contain(x => x.Code == "err.two");
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

        result.IsSuccess.Should().BeFalse(
            "Fail must set IsSuccess = false; a mutant flipping false→true is caught here");
    }

    [Fact]
    public void Fail_EnumerableProblem_IsSuccess_IsFalse()
    {
        var result = Result<string>.Fail(new[] { SomeProblem });

        result.IsSuccess.Should().BeFalse(
            "Fail(IEnumerable) must set IsSuccess = false; a mutant flipping false→true is caught here");
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

        mapped.IsSuccess.Should().BeTrue("Map on a success must yield a success");
        mapped.Value.Should().Be(14);
        transformCalled.Should().BeTrue("the transform must be invoked on a success result");
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

        mapped.IsSuccess.Should().BeFalse("Map on a failure must yield a failure");
        mapped.Problems.Should().ContainSingle()
              .Which.Code.Should().Be("test.error",
                  "the original problems must be preserved on the failure path");
        transformCalled.Should().BeFalse(
            "the transform must NOT be invoked when the input is a failed result; " +
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
        result.IsFailure.Should().BeFalse();
    }

    [Fact]
    public void IsFailure_IsTrue_WhenFailed()
    {
        var result = Result.Fail(SomeProblem);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Problems_ReturnsEmpty_WhenDefaultStruct()
    {
        var result = default(Result);
        result.Problems.Should().BeEmpty();
        result.Problems.Should().NotBeNull();
    }

    [Fact]
    public void Problems_ReturnsProblems_WhenFailed()
    {
        var result = Result.Fail(SomeProblem);
        result.Problems.Should().ContainSingle().Which.Code.Should().Be("ng.error");
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

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*CvtWrongReturnType*CvtExpectedType*");
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

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*cvt-ev1-guard*schema version 1*expects version 2*");
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
        (TagA == TagB).Should().BeTrue();
        (TagA != TagB).Should().BeFalse();
    }

    [Fact]
    public void EqualityOperator_ReturnsFalse_ForDifferentTags()
    {
        (TagA == TagC).Should().BeFalse();
        (TagA != TagC).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // GetHashCode — same value → same hash; different values → different hash
    // -----------------------------------------------------------------------

    [Fact]
    public void GetHashCode_IsSame_ForEqualTags()
    {
        TagA.GetHashCode().Should().Be(TagB.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DiffersByConceptOrId()
    {
        var byDifferentConcept = new EventTag("customer", "123");
        TagA.GetHashCode().Should().NotBe(byDifferentConcept.GetHashCode());
        TagA.GetHashCode().Should().NotBe(TagC.GetHashCode());
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
        s.Should().Be("order:123");
        s.Should().Be(TagA.Value);
    }

    // -----------------------------------------------------------------------
    // Parse — invalid format (no colon) must throw ArgumentException
    // -----------------------------------------------------------------------

    [Fact]
    public void Parse_WithoutColon_ThrowsArgumentException()
    {
        var act = () => EventTag.Parse("nocolon");
        act.Should().Throw<ArgumentException>()
           .WithMessage("*Invalid tag format*");
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
        var act = () => new EventTag("_causation", "abc");
        act.Should().Throw<ArgumentException>()
           .WithParameterName("concept")
           .WithMessage("*reserved*");
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
        (TypeA == TypeB).Should().BeTrue();
        (TypeA != TypeB).Should().BeFalse();
    }

    [Fact]
    public void EqualityOperator_ReturnsFalse_ForDifferentId()
    {
        (TypeA == TypeC).Should().BeFalse();
        (TypeA != TypeC).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // GetHashCode — different IDs produce different hashes (extremely likely)
    // -----------------------------------------------------------------------

    [Fact]
    public void GetHashCode_DiffersByEventTypeId()
    {
        TypeA.GetHashCode().Should().NotBe(TypeC.GetHashCode());
    }

    // -----------------------------------------------------------------------
    // Constructor — version < 1 must be rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_RejectsVersionLessThanOne()
    {
        var act = () => new EventType("order-placed", 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
           .WithMessage("*version must be >= 1*");
    }

    // -----------------------------------------------------------------------
    // FromType / GetEventTypeId — types without [EventType] must throw
    // -----------------------------------------------------------------------

    [Fact]
    public void FromType_ThrowsForTypeWithoutAttribute()
    {
        var act = () => EventType.FromType(typeof(CvtNoAttributeEvent));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*does not have an*[EventType]*");
    }

    [Fact]
    public void GetEventTypeId_ThrowsForTypeWithoutAttribute()
    {
        var act = () => EventTypeAttribute.GetEventTypeId(typeof(CvtNoAttributeEvent));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*does not have an*[EventType]*");
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
        s.Should().Be("order-placed");
        // Make sure version is NOT included in the string — that would break query filters.
        s.Should().NotContain("1");
    }

    [Fact]
    public void ImplicitConversion_StringToEventType_ProducesEventType()
    {
        EventType fromString = "order-placed";
        fromString.Id.Should().Be("order-placed");
        fromString.Version.Should().Be(1, "the string→EventType implicit conversion defaults to version 1");
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

        ex.ConflictingPosition.Should().Be(-1);
        ex.ExpectedPosition.Should().Be(-1);
        ex.Query.IsEmpty.Should().BeTrue(
            "the fallback constructor must set Query = DcbQuery.Empty");
        ex.Message.Should().Be("conflict happened");
        ex.InnerException.Should().BeSameAs(inner);
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

        ex.Message.Should().Be("DCB conflict");
        ex.ConflictingPosition.Should().Be(99L);
        ex.ExpectedPosition.Should().Be(50L);
        ex.Query.Should().Be(SomeQuery);
        ex.InnerException.Should().BeSameAs(inner);
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

        problem.Code.Should().Be(DcbConflictException.ProblemCode);
        problem.Message.Should().Be(ex.Message);
        problem.Details.Should().ContainKey("conflictingPosition")
               .WhoseValue.Should().Be(77L);
        problem.Details.Should().ContainKey("expectedPosition")
               .WhoseValue.Should().Be(30L);
    }

    // -----------------------------------------------------------------------
    // ToProblem on the fallback constructor — positions are -1 in the Problem too
    // -----------------------------------------------------------------------

    [Fact]
    public void ToProblem_OnFallbackException_ReportsNegativePositions()
    {
        var ex = new DcbConflictException("oops", new Exception("inner"));
        var problem = ex.ToProblem();

        problem.Code.Should().Be(DcbConflictException.ProblemCode);
        problem.Details["conflictingPosition"].Should().Be(-1L);
        problem.Details["expectedPosition"].Should().Be(-1L);
    }
}
