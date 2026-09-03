# Notes — computation expression machinery

Working notes on the F# CE features this library leans on. Kept at the repository
root rather than under `docs/`, because `fsdocs` renders every `.md` under `docs/`
into the published site and this is internal.

## `Source` — the source transformation member

`Source` is an optional builder member that the compiler inserts around the
**right-hand side of every `let!`, `and!` and `for … in`**, before that value
reaches `Bind`/`MergeSources`/`For`:

```fsharp
let! c = Options.config        // becomes  Bind(b.Source(Options.config), fun c -> …)
let! a = x  and! b = y         // becomes  BindReturn(b.MergeSources(b.Source x, b.Source y), …)
```

If the builder defines no `Source`, the expression is passed through unchanged —
the pre-F#-5 behaviour, and why it is easy to have never met it.

### Why it exists

It decouples *what may be bound* from *what the builder computes over*. Without
it, every additional bindable type needs its own `Bind` overload; with `and!`
that becomes combinatorial, since arity 2 alone would need an overload for every
pairing of bindable types. With `Source`, everything normalises to the builder's
own type first and there is exactly one `MergeSources` per arity.

This is how `task { }` accepts `Task`, `ValueTask`, `Async` and
`IAsyncEnumerable` from a single `Bind`.

### Two properties worth remembering

- **It is purely syntactic**, resolved by ordinary overload resolution at each
  binding site. It never runs unless a `let!`/`and!`/`for` mentions it —
  `return`/`return!` do not go through it.
- **It can be an extension method.** Because it is resolved by name on the
  builder type, a downstream module can write
  `type InputsBuilder with member _.Source(x: Foo) = …` and make a new type
  bindable without modifying the builder. Libraries exploit this with the
  low-priority-module trick (extensions in a nested module, shadowed by
  higher-priority ones) to control which overload wins.

### How this library uses it

`InputsBuilder` defines two:

```fsharp
member inline _.Source(input: ActionInput<'T>): InputSpec<'T> = InputSpec.ofInput input
member inline _.Source(spec: InputSpec<'T>): InputSpec<'T>    = spec
```

The first lets a bare option be bound directly (`let! cfg = Options.config`).
The second makes an existing `InputSpec` bindable as a source, which is what the
pipeline and command layers need in order to harvest a lower layer's spec into
their own.

## Related gotchas already recorded elsewhere

- Omitting `Bind` entirely is what **enforces** applicative use: a sequential
  `let!` then becomes `FS0708` rather than a silently incomplete option set. See
  `PLAN.md`, finding (4).
- `let! x = e in rest` translates to `Bind(e, fun x -> «rest»)` with **no
  `Delay` wrapper**, so the continuation's type is whatever the body's `Yield`
  returned. With heterogeneous `Yield` overloads that is a common source of
  `FS0193`. See `CLAUDE.md`.
