---
name: implement-story
description: Implement one user story in the .NET solution from its class diagram, building exactly the public service methods (and the controller/repository chain behind them) plus the private service helpers the diagram shows. Pass the user story and class diagram (PlantUML text, .puml path or .png) as arguments.
---

# Implement story skill

Arguments (`$ARGUMENTS`) contain the user story and its class diagram (PlantUML text, or a path such as `@AutoPlex_StoryN_ClassDiagram.puml` / `.png`). The diagram is the **contract**: what it shows gets built, what it doesn't show doesn't. The user story supplies the "why" and acceptance criteria; diagram notes supply the rules.

The "Story 5: Browse vehicle inventory" text at the end is a WORKED EXAMPLE of the required structure and level of detail. Apply the same steps to the story in the arguments, replacing "Vehicle" with the new entity and deriving methods, filters, SQL and formulas from the new diagram. Ignore Vehicle-specific rules the new diagram does not show. If the arguments are missing, the diagram is unreadable, or a note contradicts the story, ask before writing code. Also follow CLAUDE.md.

---

## Core rule: the service drives the scope

1. **Public service methods** = every `+` method on the `«service»` interface (e.g. `IVehicleService`). Each one is a feature. Build its full vertical slice and nothing more:
   controller action → service method → repository method → SQL.
   - No method on the interface → no endpoint, no repository method. Never add CRUD that isn't drawn (a note like "read-only story: no Add/Update/Delete" confirms this, but it holds even without the note).
   - Signatures are copied exactly: name, parameter types, return type, nullability (`Task<VehicleResponseDto?>`).
2. **Private service methods** = every `-` method on the `«service»` class (e.g. `-CalculateProfit(...)`). These hold the business logic.
   - Implement with the exact name and signature shown, `private` in C#. Make it `private static` when it uses no instance state (or a note says "static").
   - Never put them on the interface, never make them `internal`/`public` "for testing", never move the logic into the repository, controller or SQL.
   - The formulas, null rules and edge cases come from the diagram notes word for word. If a rule is missing (rounding, division by zero, casing of a status compare), ask rather than invent.
   - A note saying which public methods call the helper ("GetAll applies it to every item and GetById to the single result") is binding: wire it into exactly those methods.
3. Controller and repository exist only to serve the public service methods; keep them thin. The service is where the story's behaviour lives and where the tests focus.

## Reading the diagram

| Diagram element | Meaning in code |
|---|---|
| `«controller»`, `«service»`, `«repository»`, `«DTO»` | Layer and project the class goes into (match existing folders/namespaces) |
| `«Story N»` (N earlier than this story) | **Already exists.** Reuse it, don't recreate or change it. If it is missing from the codebase, stop and report. |
| `+` / `-` | public / private |
| `I` circle, dashed arrow with hollow triangle | Interface and the class that implements it |
| Solid line with hollow triangle | Inheritance (e.g. `VehicleParameters : RequestParameters`) |
| `[0..1]` | Nullable (`string?`, `int?`, `decimal?`, `DateTime?`) |
| `{readOnly}` | Calculated field: never read from a table column, never accepted in a request body; set only by a private service method. Still needs a public setter so the service can fill it. |
| Dashed arrow labelled `reads "x" table` | Repository queries that table; labelled `JOIN` / `VendorId` arrows tell you the join |
| Arrow `gets connection` → `IRepositoryContext` | Use the existing connection factory, as in other repositories |
| Notes | Rules: routes, status codes, response shape, SQL, filters, paging limits, formulas. Treat every sentence as a requirement. |
| Note "Story N adds X, Y" on a DTO | DTO already exists from an earlier story; add only the listed members |
| Title "(depends on Story N)" | Verify Story N's classes exist before starting |

If the diagram is a PNG, transcribe every class, member, arrow label and note into your working notes first, then confirm anything you could not read clearly.

## Tasks

0. **Inspect before writing**
   - Read CLAUDE.md and the schema file it names (e.g. `Database/CreateDB.sql`). Confirm every table and column the diagram's SQL note uses: exact names, types, nullability, defaults, allowed status values, FKs. Don't guess; don't edit the schema.
   - Find the existing classes marked `«Story N»` (RequestParameters, PagedList, MetaData, IRepositoryContext, entities) and an existing controller/service/repository to mirror (attributes, namespaces, usings, Dapper style, DI file names, test projects).
   - Detect the SQL dialect from the existing repositories and the note (e.g. `OFFSET … FETCH NEXT … ROWS ONLY` = SQL Server; `OFFSET … LIMIT` = PostgreSQL). Use the one the codebase uses; if the note disagrees with the codebase, ask.
   - Check whether any class in the diagram already exists (e.g. a DTO from an earlier story). Extend it; never duplicate.
   - Write down the list of public service methods and private service methods you will implement. This list is the scope for every later step.

1. **DTOs and parameters**
   - Response DTO: exactly the members in the diagram, with the diagram's names and nullability. Stored columns map from SQL via column aliases; `{readOnly}` members are not selected from SQL.
   - Parameters class: inherits the existing `RequestParameters`; add only the filter properties drawn. Respect paging caps from notes (e.g. "PageSize is capped at maxPageSize (50)") using the base class mechanism; if the base cap differs, override it in the derived class, don't change the shared base.
   - Create/Update DTOs only if the service interface has Add/Update methods.

2. **Repository** (only methods matching the public service methods)
   - Interface and class signatures exactly as drawn.
   - SQL exactly as the note describes: tables, JOINs, WHERE filters ("when given" = add the clause only when the parameter is present and valid), ORDER BY, paging, `COUNT(*)` for `MetaData.TotalCount`. Parameterized only; explicit column list; `using IDbConnection connection = _context.GetDatabaseConnection();` (or the codebase's equivalent); async Dapper.
   - Returns stored columns only. Leave calculated fields at their defaults; the service fills them.
   - Read-only methods: no transaction, no INSERT/UPDATE.

3. **Service** (main focus)
   - Constructor injects only what the diagram shows (e.g. `IVehicleRepository`).
   - Each public method: call the repository, apply the private helper(s) the notes assign to it, return. Preserve `PagedList` metadata when applying a helper to a page (update the items in place or rebuild with the same count/page values).
   - `null` from the repository passes through as `null` (controller turns it into 404). Never call a helper on `null`.
   - Each private method: implement the note's formulas exactly, in the order given (e.g. Total_Cost before Gross_Profit before Gross_Margin, because later ones use earlier ones). Handle the edge cases the note names (e.g. `Selling_Price == 0` → `Gross_Margin = null`). Keep it side-effect free apart from setting the calculated fields on the object it is given (or returning a new one, matching the signature).
   - Validation the story's acceptance criteria require goes in the service too, so it holds even if annotations are bypassed.

4. **Controller** (only actions matching the public service methods)
   - Same attributes and conventions as existing controllers; route from the note (e.g. `[controller]/[action]`).
   - Response shape and status codes exactly as the note says (e.g. GetAll → `200 { items, metaData }`; GetById → 200, or 404 when null).
   - No logic beyond mapping service results to HTTP results.

5. **DI registration**: register the new repository and service in the existing extension classes with the same lifetime as their neighbours.

6. **Tests**: write them with the separate `implement-story-test` skill, passing the same user story and diagram:
   `/implement-story-test <user story> @AutoPlex_StoryN_ClassDiagram.puml`
   That skill covers the service tests (private methods tested through the public methods), the repository tests and the controller tests. If I ask for tests in the same run, follow that skill's steps after the build passes.

## Constraints

- Build only what the diagram shows. No extra endpoints, repository methods, DTO members, filters or helpers. If you think something is missing, say so in the summary instead of adding it.
- Don't change classes marked `«Story N»` from earlier stories, or any other story's behaviour.
- Don't modify the schema file or add migrations unless asked.
- Match existing style, namespaces and usings. No new NuGet packages, no ORM/AutoMapper/MediatR.
- Run `dotnet build` from the solution root and fix errors.
- Don't commit unless asked.

## Final summary (always)

1. **Traceability table**: each diagram element (class, `+`/`-` method, note rule) → file and member that implements it → test that covers it.
2. Endpoints added: method, URL, query params, response shape, status codes.
3. Private service methods: name, formula/rules implemented, which public methods call them, edge cases tested.
4. Schema facts used: tables, columns, types, JOINs, status values, defaults.
5. Anything ambiguous, assumed or deliberately not built.

---

## Worked example: Story 5, Browse vehicle inventory (depends on Story 4)

**User story (example)**: As a sales manager, I want to browse and search the vehicle inventory and see each vehicle's cost, profit and margin, so that I can price and sell vehicles. Acceptance: filter by status, search by VIN/make/model, profit uses total cost, margin is "unavailable" when there is no selling price.

**Scope extracted from the diagram**

- Public service methods (`IVehicleService`):
  - `GetAllVehicles(VehicleParameters parameters) : Task<PagedList<VehicleResponseDto>>`
  - `GetVehicleById(long id) : Task<VehicleResponseDto?>`
- Private service methods (`VehicleService`):
  - `-CalculateProfit(VehicleResponseDto vehicle) : VehicleResponseDto` (static helper, not on the interface)
- Not built: Add (Story 4), Update, Delete (note: "Read-only story").
- Existing, reuse only: `Vehicle` (Story 4), `Vendor` (Story 3), `RequestParameters`, `PagedList`, `MetaData`, `IRepositoryContext` (Story 1).

**DTOs**
- `VehicleParameters : RequestParameters` with `string? Status`, `string? SearchTerm`. Status is `"available"` or `"reserved"`, empty = all (`"sold"` comes in Story 8, don't accept it yet). PageSize capped at 50.
- `VehicleResponseDto` exists from Story 4: add `Vendor_Name`, `Gross_Profit`, `Gross_Margin` (`decimal?`), `Is_Available`. `Total_Cost`, `Gross_Profit`, `Gross_Margin`, `Is_Available` are `{readOnly}`: not selected from SQL.

**Repository** (`IVehicleRepository` / `VehicleRepository`, Dapper + raw SQL)
- GetAll: `vehicles` JOIN `vendors` for `vendor_name`; `WHERE status = @Status` (when given) `AND (vin LIKE @SearchTerm OR make LIKE @SearchTerm OR model LIKE @SearchTerm)` (when given, `%term%`, case-insensitive), both together; `ORDER BY make, model`; `OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY`; plus `COUNT(*)` with the same WHERE for `MetaData.TotalCount`.
- GetById: same SELECT/JOIN, `WHERE id = @Id`; null when not found.
- No INSERT/UPDATE, no transaction.

**Service** (`VehicleService`)
- `GetAllVehicles`: repository page → `CalculateProfit` on every item → return the same `PagedList` (metadata intact).
- `GetVehicleById`: repository result → `null` stays `null`, else `CalculateProfit(result)`.
- `private static VehicleResponseDto CalculateProfit(VehicleResponseDto vehicle)`, in this order:
  - `Total_Cost = Purchase_Price + Additional_Costs`
  - `Gross_Profit = Selling_Price - Total_Cost`
  - `Gross_Margin = Selling_Price == 0 ? null : Gross_Profit / Selling_Price * 100`
  - `Is_Available = Status == "available"` (ask about case-sensitivity if the schema doesn't fix the casing)
  - Profit and margin use `Total_Cost`, never `Purchase_Price` alone. No rounding unless the story asks.

**Controller** (`VehicleController`, route `[controller]/[action]`)
- `GET /Vehicle/GetAllVehicles?PageNumber=&PageSize=&Status=&SearchTerm=` → `200 { items, metaData }`
- `GET /Vehicle/GetVehicleById?id=` → `200 VehicleResponseDto` or `404`

