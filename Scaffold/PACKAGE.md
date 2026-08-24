# StarterAspMVCEditorTemplates.ScaffoldTool

CRUD generation for ASP.NET Core MVC projects built on
[StarterAspMVCEditorTemplates](https://www.nuget.org/packages/StarterAspMVCEditorTemplates/).

**This tool only makes sense in a project that has those editor templates.** It
deliberately emits almost no presentation of its own: no `<input>` tags, no
hardcoded CSS classes. It produces annotated ViewModels and explicit
`Html.EditorFor` calls, and lets `Views/Shared/EditorTemplates` do the rendering.
Point it at a project without them and every property falls back to MVC's
built-in defaults, which is not what you want.

## Install

```bash
dotnet tool install --global StarterAspMVCEditorTemplates.ScaffoldTool
```

## Use

Works like `dotnet ef`: change to the folder containing your `.csproj`.

```bash
cd src/MyApp

scaffold inspect          # reads EF Core's model -> model.json
scaffold generate --dry-run
scaffold generate --force
```

`inspect` builds a throwaway probe project that references yours, so it reads
`IModel` using **your** EF Core version. Re-running merges: edits you made to
labels, search configuration, field order and `additionalViewData` survive a
migration, while schema facts are re-derived.

`model.json` is meant to be committed and edited. It is where you correct a
display column the conventions guessed wrong, translate labels, reorder fields,
or turn a search filter off.

## What you get, per entity

```
ViewModels/{Entity}CreateModel.cs      ViewModels/{Entity}SearchModel.cs
ViewModels/{Entity}EditModel.cs        ViewModels/{Entity}IndexModel.cs
ViewModels/{Entity}ListItemModel.cs    Controllers/{Route}Controller.cs
ViewModels/{Entity}DetailsModel.cs
Views/{Route}/Index Create Edit Details Delete .cshtml
```

- Index searchable by property, sortable, paged — rendered on the templates you
  already have, with no additions to `EditorTemplates`
- Async action names throughout
- Foreign keys as `Dropdown`, with generated select-list providers
- Many-to-many as `CheckboxList`, reconciled on save rather than cleared
- Composite keys, explicit join entities, natural keys
- Optimistic concurrency when a `RowVersion` exists, and only then
- Cascade delete warnings that state how many rows go with it

## Commands

| | |
|---|---|
| `scaffold inspect` | read the EF model into `model.json` |
| `scaffold generate` | render files; `--dry-run`, `--force`, `--entity` |
| `scaffold doctor` | which binary is running and what shipped in it |
| `scaffold eject-rules` | copy the naming conventions out to edit |
| `scaffold eject-templates` | copy the Scriban templates out to edit |

An ejected template always wins over the built-in one, so house style is a file
edit rather than a fork.

## Requirements

- .NET 10 SDK
- EF Core, with the project in a buildable state (`inspect` builds it)
- `Views/Shared/EditorTemplates` from StarterAspMVCEditorTemplates

## Known gaps

Owned types are flattened to scalars unless mapped explicitly. Collection
navigations are Details-only, with no inline add and remove. Many-to-many is
neither searchable nor a grid column. Large lookup tables always render as a
dropdown regardless of row count.
