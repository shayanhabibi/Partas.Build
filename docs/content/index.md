---
title: Partas.Build
description: Composable build workflows and command-line tools in F#.
layout: splash
order: 0
---

<section class="pb-hero">
<div>
<span class="pb-eyebrow">F# · Build workflows · Command-line tools</span>
<h1>A build you can<br/>read and reuse.</h1>
<p class="pb-hero__summary">Define stages once. Compose them into commands. The inputs each stage reads become your CLI options, validation, and help.</p>
<div class="pb-actions">
<a class="pb-button pb-button--primary" href="/Partas.Build/build/getting-started/">Get started →</a>
<a class="pb-button" href="/Partas.Build/reference/">API reference</a>
</div>
</div>
<div class="pb-demo" aria-label="A build command and its automatically registered configuration option">
<div class="pb-demo__label">Build.fs</div>
<pre><code><span class="k">let</span> build = <span class="k">input</span> {
    <span class="k">let!</span> config = Input.option&lt;string&gt; <span class="s">"--configuration"</span>
                  |&gt; Input.def <span class="s">"Release"</span>
    <span class="k">return</span> stage <span class="s">"compile"</span> {
        run (cmd $<span class="s">"dotnet build -c {config}"</span>)
    }
}

<span class="k">let</span> root = Command.root {
    command <span class="s">"build"</span> &#123; build &#125;
}</code></pre>
<div class="pb-demo__output">$ build build --help<br/>Options: --configuration &lt;value&gt; [default: Release]</div>
</div>
</section>

## One definition, several ways to use it

::::cards
:::card title="Compose your workflow" href="/Partas.Build/build/composition/"
Stages and pipelines are F# values. Nest them, yield lists, and share them across commands and files.
:::
:::card title="Start with ready-made stages" href="/Partas.Build/build/baked/"
Baked supplies restore, build, pack, test, and publish stages. ShipIt adds changelog-driven release workflows.
:::
:::card title="Inspect before you run" href="/Partas.Build/build/agents/"
Generated help, command schemas, execution plans, and JSON results make the same build usable by people and agents.
:::
::::

<section class="pb-band">

## From a script to a hosted build

Use a `.fsx` script or a build project. Keep a reusable root in a long-lived F# session and invoke it with typed results. Partas.Build targets `net10.0`, `net8.0`, and `netstandard2.0`.

[Installation](Build/installation.md) · [Hosting](Build/hosting.md) · [ShipIt extension](extensions/shipit.md) · [External annotations](external-annotations/index.md)

</section>
