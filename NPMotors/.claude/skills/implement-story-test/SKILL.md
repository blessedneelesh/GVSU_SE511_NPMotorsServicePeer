---
name: implement-story-test
description: Write the service, repository and controller tests for a user story already implemented with implement-story in NPMotors, focusing on the public service methods and the private service helpers in the class diagram. Pass the user story and class diagram (PlantUML text, .puml path or .png) as arguments.
---

# Implement story test skill

Arguments (`$ARGUMENTS`) contain the user story and its class diagram (PlantUML text, or a path such as `@AutoPlex_StoryN_ClassDiagram.puml` / `.png`). The production code for the story should already exist (usually written with `/implement-story`). This skill only writes tests.

The diagram is the **test contract**:
- every `+` method on the `«service»` interface gets tests;
- every `-` method on the `«service»` class gets tests, run through the public methods that call it;
- every rule in a note (formula, edge case, filter, paging limit, status code, response shape) gets at least one test.

The "Story 5: Browse vehicle inventory" text at the end is a WORKED EXAMPLE of the required structure and level of detail. Apply the same steps to the story in the arguments, replacing "Vehicle" with the new entity and deriving cases and expected values from the new diagram and story. Ignore Vehicle-specific cases the new diagram does not call for. If the arguments are missing, the diagram is unreadable, or the implementation isn't there yet, ask before writing tests. Also follow CLAUDE.md.

---

## Tasks

0. **Inspect before writing**
   - Read CLAUDE.md, the schema file (e.g. `NPMotors.Repository/Database/CreateDB.sql`) and the story's production code: controller, service interface and class, repository interface and class, DTOs, parameters.
   - Check that the code matches the diagram (method names, signatures, private helpers). If something in the diagram is missing from the code, stop and report it; don't write tests for code that doesn't exist and don't implement it here.
   - Read the existing customer/vendor tests in each test project. Copy their test framework (xUnit/NUnit/MSTest), mocking library, assertion style, naming convention, fixtures, DB setup and cleanup.
   - Build a **test plan** before writing code: list each diagram element (`+` method, `-` method, note rule, acceptance criterion) and the test name(s) that will cover it. Every element needs at least one test.

1. **Service tests (required, main focus)**: `<ServiceTestProject>/Service/<Entity>ServiceTest.cs`
   - Use the service test project if the solution has one. If not, use a `Services` folder in `Module.Tests`. Don't create a new test project without asking.
   - Mock `I<Entity>Repository` with the mocking library the existing tests already use. If they use none, write a small hand-written fake repository in the test file. No new NuGet packages.
   - Test every private method **only through the public methods that call it**. Never use reflection, `InternalsVisibleTo` or a widened access modifier.
   - Work out every expected value by hand from the note's formula and write it as a literal in the test. Don't recompute it in the test using the same formula as the code.
   - For each private method:
     - one test per formula with normal values, checking the exact expected number;
     - one test per edge case the notes name (zero divisor, null input field, negative result, boundary status value);
     - one test showing which inputs the formula uses and which it doesn't (e.g. profit changes when `Additional_Costs` changes);
     - one test per status/flag value for boolean fields (e.g. `Is_Available` true/false).
   - For each public method:
     - GetAll: the helper is applied to **every** item, and `MetaData` (TotalCount, CurrentPage, PageSize, TotalPages) is unchanged;
     - GetAll with an empty page returns an empty list with correct MetaData and doesn't fail;
     - GetById: the helper is applied to the single result;
     - GetById: a `null` from the repository returns `null`;
     - the parameters object / id is passed to the repository unchanged (verify the mock call).
   - Acceptance-criteria validation in the service (if any): one passing and one failing test per rule.

2. **Repository tests**: `NPMotors.Repository.Tests/DataAccess/<Entity>RepositoryTest.cs` (runs against the test DB like the customer tests)
   - each filter on its own (e.g. Status), invalid filter values ignored, filters combined with AND;
   - search: case-insensitive, matches each searched column, partial match;
   - paging: second page returns the right rows; `MetaData.TotalCount` counts all matches, not just the page; PageSize above the cap is clamped;
   - ORDER BY from the note;
   - empty result returns an empty `PagedList`, not null;
   - JOINed columns populated (e.g. `Vendor_Name`);
   - `{readOnly}` calculated fields are still at their default values coming out of the repository (they are the service's job);
   - GetById: found returns the row; unknown id returns null.
   - Write tests for write methods (Add/Update/Delete) only if the service interface in the diagram has them.

3. **Controller tests**: `Module.Tests/Controllers/<Entity>ControllerTest.cs`
   - GetAll: 200 with `items` and `metaData` in the shape from the note;
   - query parameters bind (PageNumber, PageSize, every filter);
   - GetById: 200 with the DTO, including calculated fields; 404 for an unknown id;
   - invalid input (e.g. non-numeric id) returns 400 via `[ApiController]`;
   - the actions **not** in the diagram don't exist (e.g. POST/PUT/DELETE return 404 or 405) when the story is read-only.

4. **Run**
   - `dotnet build` from the solution root and fix test compile errors.
   - Run the new service and controller tests. Run the repository tests if the DB is available; if it isn't, say so.

## Constraints

- **Don't change production code.** If a test fails because the code is wrong (it doesn't match the diagram, a note or the story), leave the test failing, and report the test, the expected and actual values, and the diagram rule it checks. Fix the code only if I ask.
- Don't weaken or delete a test to make it pass.
- Don't modify the schema file or the existing tests of other entities.
- Test data must satisfy the constraints in the schema file (FKs, CHECK values; e.g. insert the referenced vendor before the vehicle). Clean up inserted rows like the existing tests do; tests must not depend on each other or on run order.
- Test names follow the existing convention (e.g. `MethodName_Scenario_ExpectedResult`).
- No new NuGet packages or test projects without asking. Don't commit unless asked.

## Final summary (always)

1. **Coverage table**: each diagram element (`+` method, `-` method, note rule, acceptance criterion) → test name(s) → pass / fail / not run.
2. Files added, with the number of tests in each.
3. Test results: passed, failed (with expected vs actual and the rule it checks), skipped and why (e.g. DB unavailable).
4. Gaps: rules in the diagram or story that could not be tested, and anything ambiguous (e.g. rounding, status casing) that you assumed.

---

## Worked example: Story 5, Browse vehicle inventory (depends on Story 4)

**What the diagram gives us to test**

- Public: `IVehicleService.GetAllVehicles(VehicleParameters)`, `IVehicleService.GetVehicleById(long)`
- Private: `VehicleService.CalculateProfit(VehicleResponseDto)`, applied by GetAll to every item and by GetById to the single result
- Note rules: `Total_Cost = Purchase_Price + Additional_Costs`; `Gross_Profit = Selling_Price - Total_Cost`; `Gross_Margin = Gross_Profit / Selling_Price × 100`, null when `Selling_Price = 0`; `Is_Available = (Status == "available")`; Status filter "available" | "reserved" (empty = all); SearchTerm matches VIN, make or model (contains, case-insensitive); PageSize capped at 50; `ORDER BY make, model`; JOIN vendors for `Vendor_Name`; GetAll → 200 `{ items, metaData }`; GetById → 200 or 404; read-only story (no Add/Update/Delete).

`VehicleServiceTest.cs` (mocked `IVehicleRepository`; `CalculateProfit` tested only through the two public methods)

| Test | Input | Expected |
|---|---|---|
| `GetVehicleById_CalculatesTotalCost` | Purchase 10 000, Additional 1 000 | Total_Cost 11 000 |
| `GetVehicleById_GrossProfitUsesTotalCost` | Purchase 10 000, Additional 1 000, Selling 14 000 | Gross_Profit 3 000 (not 4 000) |
| `GetVehicleById_CalculatesGrossMargin` | same as above | Gross_Margin = 3 000 / 14 000 × 100 (≈ 21.43, unrounded) |
| `GetVehicleById_SellingPriceZero_MarginNull` | Selling 0 | Gross_Margin null, Gross_Profit = −Total_Cost |
| `GetVehicleById_NegativeProfit_NegativeMargin` | Purchase 10 000, Additional 3 000, Selling 12 000 | Gross_Profit −1 000, margin negative, not clamped |
| `GetVehicleById_ZeroAdditionalCosts` | Additional 0 | Total_Cost = Purchase_Price |
| `GetVehicleById_StatusAvailable_IsAvailableTrue` | Status "available" | Is_Available true |
| `GetVehicleById_StatusReserved_IsAvailableFalse` | Status "reserved" | Is_Available false |
| `GetVehicleById_NotFound_ReturnsNull` | repository returns null | null, no exception |
| `GetVehicleById_PassesIdToRepository` | id 42 | repository called once with 42 |
| `GetAllVehicles_CalculatesEveryItem` | page of 3 vehicles with different prices | each item has its own correct Total_Cost/Profit/Margin/Is_Available |
| `GetAllVehicles_KeepsMetaData` | TotalCount 12, page 2, size 5 | MetaData identical after calculation |
| `GetAllVehicles_EmptyPage` | 0 items, TotalCount 0 | empty list, MetaData TotalCount 0 |
| `GetAllVehicles_PassesParametersToRepository` | Status "available", SearchTerm "civic" | repository receives the same parameters object |

`NPMotors.Repository.Tests/DataAccess/VehicleRepositoryTest.cs` (insert a test vendor, then test vehicles; delete both afterwards)
- `GetAllVehicles_StatusFilter_ReturnsOnlyThatStatus` ("available", "reserved")
- `GetAllVehicles_NoStatus_ReturnsAll`
- `GetAllVehicles_SearchTerm_MatchesVin` / `_MatchesMake` / `_MatchesModel` (partial, mixed case such as "CiV")
- `GetAllVehicles_StatusAndSearch_BothApply`
- `GetAllVehicles_OrderedByMakeThenModel`
- `GetAllVehicles_Paging_SecondPageAndTotalCount` (TotalCount = all matches)
- `GetAllVehicles_PageSizeAbove50_IsCapped`
- `GetAllVehicles_NoMatches_ReturnsEmptyPagedList`
- `GetAllVehicles_IncludesVendorName` (from the JOIN)
- `GetAllVehicles_CalculatedFieldsNotFilled` (Gross_Profit 0, Gross_Margin null, Is_Available false straight from the repository)
- `GetVehicleById_Existing_ReturnsVehicleWithVendorName`
- `GetVehicleById_Unknown_ReturnsNull`

`Module.Tests/Controllers/VehicleControllerTest.cs`
- `GetAllVehicles_Returns200WithItemsAndMetaData`
- `GetAllVehicles_BindsQueryParameters` (PageNumber, PageSize, Status, SearchTerm)
- `GetVehicleById_Existing_Returns200WithCalculatedFields`
- `GetVehicleById_Unknown_Returns404`
- `GetVehicleById_NonNumericId_Returns400`
- `NoWriteEndpoints_AddUpdateDelete_NotMapped` (POST/PUT/DELETE on `/Vehicle/...` return 404 or 405)
