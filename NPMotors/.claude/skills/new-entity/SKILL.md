---
name: new-entity
description: Implement a new entity's endpoints (DTOs, parameters, repository, service, controller, DI, tests) in NPMotors following the Customer pattern. Pass the user story and class diagram as arguments.
---

# New entity skill

Arguments (`$ARGUMENTS`) contain the NEW user story and its class diagram (PlantUML text or a path such as `@AutoPlex_StoryN_ClassDiagram.puml`). Use them as the source of truth for the entity name, fields, acceptance criteria and rules.

The "Vendors" text below is a WORKED EXAMPLE of the required structure and level of detail. Apply the same steps, conventions, constraints and final summary to the entity in the arguments: replace every "Vendor" / "vendor" with the new entity, derive its filters, validation and acceptance-criteria checks from the arguments, and ignore any Vendor-specific rule (Type, GetActiveVendors, balance stories) that the new story does not call for. If the arguments are missing or ambiguous, ask before writing code. Also follow CLAUDE.md.

---

## User story (Vendors) 

  

As a purchasing officer, 

I want to: 

* Create, edit, and view vendors 

* Search vendors and set them Active or Inactive 

  

So that: 

* Vehicle and parts purchases can reference the supplier 

  

Not yet: vendor balance and totalPurchases stay 0 until Stories 14 to 16. 

  

Acceptance criteria: 

* Vendor name is required. 

* Type is one of Vehicle, Parts, or Service. 

* Inactive vendors cannot be selected on new purchases. 

* Balance and totalPurchases are read-only and default to 0. 

  

## Task 

  

Implement GetAll (with search), GetById, Add, Update and Delete vendor endpoints (plus GetActiveVendors) in the NPMotors .NET solution. Follow exactly the layering and conventions already used by the Customer endpoints. Vendors are set Active or Inactive through Add and Update; there is no separate status endpoint. 

  

## Schema source 

  

- The database schema is defined in NPMotors.Repository/Database/CreateDB.sql. Treat it as the single source of truth for table names, column names, types, defaults, constraints and foreign keys. 

- Read the `vendor` table definition and any tables that reference it from this file before writing DTOs or SQL. Do not guess column names or types. 

  

## Existing flow to mirror 

  

- Controller: NPMotors.Peer/Controllers/CustomerController.cs 

  - [ApiController], [Route("[controller]/[action]")], injects ICustomerService, async actions returning Task<IActionResult>. 

  - GetAllCustomers([FromQuery] CustomerParameters p) returns Ok(new { items = customers, metaData = customers.MetaData }). 

- Service interface: NPMotors.Services.Abstraction/ServiceInterfaces/ICustomerService.cs 

- Service: NPMotors.Services/Service/CustomerService.cs (thin pass-through) 

- Repository interface and implementation: NPMotors.Repository/Interface/ICustomerRepository.cs and Repositories/CustomerRepository.cs 

  - Dapper, `using IDbConnection connection = _repositoryContext.GetDatabaseConnection();`, parameterized SQL only, snake_case columns, PostgreSQL. 

  - GetAllCustomers builds whereClauses + DynamicParameters, runs a COUNT(*) query, then the page query with OFFSET @Offset LIMIT @Limit, and returns new PagedList<T>(items, count, pageNumber, pageSize). 

- Request features: NPMotors.Repository/RequestFeatures/ 

  - RequestParameters (abstract; PageNumber defaults to 1, PageSize defaults to 5, capped at 100) 

  - CustomerParameters : RequestParameters (Status, SearchTerm) 

  - PagedList<T> : List<T> with MetaData 

- DTOs: NPMotors.Repository/Models/Dto/ (CustomerResponseDto, CustomerCreateDto, CustomerUpdateDto). Follow the same property naming and column-aliasing approach (e.g. `vendor_name AS Name` in a CustomerColumns-style constant). 

- Customers also have Balance and TotalPurchases as server-controlled fields; handle vendor balance/total_purchases the same way. 

- Id type: customers use `long id`. For vendors, use the id type defined for the `vendor` table in CreateDB.sql. 

  

## Tasks 

  

0. Check the schema in NPMotors.Repository/Database/CreateDB.sql first 

   - From the `CREATE TABLE vendor` statement, confirm: 

     - exact table and column names (name vs vendor_name, contact fields, address fields) 

     - id type and whether it's generated (SERIAL/BIGSERIAL/IDENTITY/uuid default) or supplied 

     - column types (numeric/decimal -> decimal), nullability and defaults (balance and total_purchases should default to 0) 

     - how type is stored and its allowed values (expected: Vehicle, Parts, Service — CHECK constraint, enum type, or plain text) 

     - how status is stored and its allowed values (expected: Active, Inactive) 

     - any CHECK or UNIQUE constraints (e.g. unique vendor name or email) 

   - Search CreateDB.sql for every `REFERENCES vendor` (e.g. vehicle purchase, parts purchase tables) and note the ON DELETE behavior. 

   - If the `vendor` table is missing from CreateDB.sql, or lacks columns the story needs (name, type, status, balance, total_purchases), stop and tell me what's missing and what you would assume. Don't edit CreateDB.sql or create migrations unless I ask. 

   - Check whether any Vendor controller/service/repository/DTO already exists; extend rather than duplicate. 

  

1. DTOs and request parameters 

   - VendorResponseDto: Id, Name, Type, Contact_Name, Phone, Email, Address, City, State, Zip, Balance, TotalPurchases, Status (use the real columns from CreateDB.sql; drop or rename properties that don't exist). 

   - VendorCreateDto / VendorUpdateDto: editable fields only, including Status (Active/Inactive is set through Add and Update). Exclude Id, Balance and TotalPurchases (server-controlled, read-only). 

   - DataAnnotations: 

     - [Required] + [StringLength] on Name (reject empty/whitespace). 

     - Type [Required] and restricted to Vehicle, Parts, Service (or the values CreateDB.sql defines), case-insensitive on input, stored in the canonical form the schema expects. 

     - Status restricted to the allowed values from CreateDB.sql. 

     - [EmailAddress] on Email if present; [StringLength] matching the VARCHAR lengths in CreateDB.sql for text columns. 

   - Add VendorParameters : RequestParameters in NPMotors.Repository/RequestFeatures/ with: 

     - string? Status 

     - string? Type 

     - string? SearchTerm 

  

2. Repository layer 

   - IVendorRepository: 

     - Task<PagedList<VendorResponseDto>> GetAllVendors(VendorParameters vendorParameters); 

     - Task<VendorResponseDto?> GetVendorById(<idType> id); 

     - Task<IEnumerable<VendorResponseDto>> GetActiveVendors(string? type = null); 

     - Task<VendorResponseDto> AddVendor(VendorCreateDto vendor); 

     - Task<VendorResponseDto?> UpdateVendor(<idType> id, VendorUpdateDto vendor); 

     - Task<bool> DeleteVendor(<idType> id); 

     - Task<bool> IsVendorReferenced(<idType> id); (checks every table that references vendor in CreateDB.sql) 

   - VendorRepository.GetAllVendors mirrors CustomerRepository.GetAllCustomers: 

     - whereClauses + DynamicParameters, all filters parameterized. 

     - Status filter: only if the value is one of the allowed values from CreateDB.sql, case-insensitive, `LOWER(status) = @Status`. Ignore invalid values like customers do. 

     - Type filter: only if the value is one of the allowed types, case-insensitive, `LOWER(type) = @Type`. Ignore invalid values. 

     - Search: trimmed term, `LIKE @SearchTerm` against LOWER(name), LOWER(contact_name), LOWER(email), LOWER(phone), LOWER(city) (use the real column names), with %term% lowercased. Combined with AND alongside the other filters. 

     - Run `SELECT COUNT(*) FROM vendor{whereSql}`, then an explicit column list (not SELECT *) with `ORDER BY name, id OFFSET @Offset LIMIT @Limit`. 

     - Return PagedList<VendorResponseDto>. 

   - GetActiveVendors: active vendors only, optionally filtered by type, ordered by name (used by the purchase vendor picker). 

   - Add: INSERT ... RETURNING <columns>. Never write balance or total_purchases; let the column defaults (0) apply. If CreateDB.sql has no default, insert 0 explicitly. 

   - Update: UPDATE ... WHERE id = @Id RETURNING <columns>, null if no row matched. Updates status along with the other editable fields. Never update balance or total_purchases. 

   - Delete: rowsAffected > 0. 

   - Parameterized queries, `using IDbConnection`, async Dapper methods. 

  

3. Service layer 

   - IVendorService (same namespace as ICustomerService) and VendorService delegating to IVendorRepository; GetAll takes VendorParameters. 

   - Re-check acceptance criteria in Add/Update (name not blank, type in allowed list, status in allowed list) so they hold even if annotations are bypassed. 

   - Normalize Type and Status to the canonical casing the schema expects before saving. 

   - Delete: if IsVendorReferenced is true, don't delete; signal a conflict so the controller returns 409 (setting the vendor Inactive via Update is the expected alternative). Use the codebase's existing result/exception pattern; if none, a small result enum. 

  

4. Controller (VendorController, same attributes/conventions as CustomerController) 

   - [HttpGet] GetAllVendors([FromQuery] VendorParameters vendorParameters) -> Ok(new { items = vendors, metaData = vendors.MetaData }) 

     Query params: PageNumber, PageSize, Status, Type, SearchTerm. 

   - [HttpGet] GetVendorById([FromQuery] <idType> id) -> Ok(vendor) or NotFound() 

   - [HttpGet] GetActiveVendors([FromQuery] string? type) -> Ok(list) 

   - [HttpPost] AddVendor([FromBody] VendorCreateDto dto) -> match what AddCustomer returns 

   - [HttpPut] UpdateVendor([FromQuery] id, [FromBody] VendorUpdateDto dto) -> Ok(updated) or NotFound() 

   - [HttpDelete] DeleteVendor([FromQuery] id) -> NoContent(), NotFound(), or Conflict() with a short message if referenced 

   - Validation failures return 400 via [ApiController]. 

   - Balance and TotalPurchases appear in responses only; any values sent in a request body are ignored. 

  

5. DI registration 

   - Register in RepositoryServiceCollectionExtensions.cs and ServicesServiceCollectionExtensions.cs with the same lifetime as the customer registrations. 

  

6. Tests (mirror the customer test projects) 

   - NPMotors.Repository.Tests/DataAccess/VendorRepositoryTest.cs: search (case-insensitive), status filter, type filter, paging + metadata, empty results, new vendor has balance and total_purchases = 0, update doesn't change balance/total_purchases, UpdateVendor can change status to Inactive, GetActiveVendors excludes inactive. 

   - Module.Tests/Controllers/VendorControllerTest.cs: GetAll returns 200 with items and metaData, invalid type returns 400, blank name returns 400, plus basic CRUD. 

   - Test data must satisfy the constraints in CreateDB.sql. Clean up inserted test data like the customer tests do. 

  

## Constraints 

- NPMotors.Repository/Database/CreateDB.sql is read-only for this task; don't modify it unless I ask. 

- Don't change any customer or employee code or behavior. 

- Don't change purchase endpoints. (Enforcing "inactive vendors can't be selected on new purchases" at purchase creation is a follow-up; GetActiveVendors supports it for now.) 

- Don't add any logic that changes vendor balance or total_purchases (Stories 14 to 16). 

- Don't add a separate status endpoint; status changes go through UpdateVendor. 

- Match existing style, usings, namespaces. No new NuGet packages, no ORM/AutoMapper/MediatR. 

- Build with `dotnet build` from the solution root and fix errors. Run the new tests if the DB is available. 

- When finished, summarize files changed and endpoints added (method, URL, query params, request/response shape, status codes), and list the vendor columns, id type, type and status values, defaults and FK references you found in CreateDB.sql. 

- Don't commit unless I ask. 