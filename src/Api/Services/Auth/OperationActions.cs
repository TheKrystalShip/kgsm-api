namespace TheKrystalShip.Api.Services.Auth;

/// <summary>One action an operation takes, as <c>GET /api/v1/operations</c> publishes it.</summary>
/// <param name="Action">The action — or a template naming a route parameter, <c>{leaf}:config.write</c>.</param>
/// <param name="Field">The body field that picks this entry, or null when the route is the action.</param>
/// <param name="Value">The field's value this entry is for; null with a field means "whenever the body carries it".</param>
/// <param name="RouteParameter">A route parameter this entry is for one value of, published as a literal segment.</param>
/// <param name="RouteValue">That parameter's value.</param>
public sealed record OperationAction(
    string Action, string? Field = null, string? Value = null, string? RouteParameter = null, string? RouteValue = null);

/// <summary>
/// An endpoint whose action is decided by its request rather than fixed on the route, declaring every
/// action it can decide — from the same function its handler decides with.
/// </summary>
/// <remarks>
/// <see cref="RequiresActionAttribute"/> covers an endpoint that is one action. These cover the rest,
/// and each derives its entries from the code the handler enforces with, so what the operations publish
/// and what the handler checks cannot come apart.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public abstract class OperationActionsAttribute : Attribute
{
    /// <summary>Every action this endpoint can require.</summary>
    public abstract IEnumerable<OperationAction> Entries();
}

/// <summary>A lifecycle command: one action per verb, from <see cref="ActionIds.ForVerb"/>.</summary>
public sealed class ActionByVerbAttribute : OperationActionsAttribute
{
    /// <summary>The body field naming the verb.</summary>
    public const string Field = "verb";

    /// <inheritdoc />
    public override IEnumerable<OperationAction> Entries() =>
        ActionIds.Verbs.Select(v => new OperationAction(ActionIds.ForVerb(v)!, Field, v));
}

/// <summary>
/// A leaf's configuration surface: the leaf's own action, named from the route's <c>{leaf}</c>, and the
/// engine's for the engine — from <see cref="ActionIds.ConfigAction"/>.
/// </summary>
/// <param name="write">Whether the endpoint changes the configuration rather than reading it.</param>
public sealed class LeafConfigActionAttribute(bool write) : OperationActionsAttribute
{
    /// <summary>The route parameter naming the leaf.</summary>
    public const string Parameter = "leaf";

    /// <summary>Whether the endpoint writes.</summary>
    public bool Write { get; } = write;

    /// <inheritdoc />
    public override IEnumerable<OperationAction> Entries() =>
    [
        new OperationAction(ActionIds.ConfigAction("{" + Parameter + "}", Write)),
        new OperationAction(ActionIds.ConfigAction(ActionIds.EngineLeaf, Write),
            RouteParameter: Parameter, RouteValue: ActionIds.EngineLeaf),
    ];
}

/// <summary>
/// A further action an endpoint requires whenever its body carries <see cref="Field"/>, on top of the
/// route's own. The handler reads this attribute to enforce it.
/// </summary>
/// <param name="field">The body field.</param>
/// <param name="action">The action it adds.</param>
public sealed class ActionWhenPresentAttribute(string field, string action) : OperationActionsAttribute
{
    /// <summary>The body field.</summary>
    public string Field { get; } = field;

    /// <summary>The action it adds.</summary>
    public string Action { get; } = action;

    /// <summary>This endpoint's entry for <paramref name="field"/>.</summary>
    public static string For(HttpContext http, string field) =>
        http.GetEndpoint()?.Metadata.GetOrderedMetadata<ActionWhenPresentAttribute>().FirstOrDefault(a => a.Field == field)?.Action
        ?? throw new InvalidOperationException($"{http.Request.Method} {http.Request.Path} declares no action for '{field}'.");

    /// <inheritdoc />
    public override IEnumerable<OperationAction> Entries() => [new OperationAction(Action, Field)];
}
