using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCrmSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workspaces_UserAccounts_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiModelConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ModelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiModelConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiModelConfigurations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiPromptTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PurposeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPromptTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiPromptTemplates_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Website = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Companies_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    KindCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.CheckConstraint("CK_Documents_Sha256", "\"Sha256\" IS NULL OR (char_length(\"Sha256\") = 64 AND \"Sha256\" ~ '^[0-9A-Fa-f]{64}$')");
                    table.CheckConstraint("CK_Documents_SizeBytes", "\"SizeBytes\" >= 0");
                    table.ForeignKey(
                        name: "FK_Documents_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Educations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstitutionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Degree = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FieldOfStudy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    StartedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Educations", x => x.Id);
                    table.CheckConstraint("CK_Educations_EndedOn", "\"EndedOn\" IS NULL OR \"StartedOn\" IS NULL OR \"EndedOn\" >= \"StartedOn\"");
                    table.ForeignKey(
                        name: "FK_Educations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Experiences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    StartedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiences", x => x.Id);
                    table.CheckConstraint("CK_Experiences_EndedOn", "\"EndedOn\" IS NULL OR \"StartedOn\" IS NULL OR \"EndedOn\" >= \"StartedOn\"");
                    table.ForeignKey(
                        name: "FK_Experiences_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    Role = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RepositoryUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    WebsiteUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    StartedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.CheckConstraint("CK_Projects_EndedOn", "\"EndedOn\" IS NULL OR \"StartedOn\" IS NULL OR \"EndedOn\" >= \"StartedOn\"");
                    table.ForeignKey(
                        name: "FK_Projects_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CategoryCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Skills_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceConfigurations", x => x.Id);
                    table.CheckConstraint("CK_SourceConfigurations_ConfigurationJson", "\"ConfigurationJson\" IS NULL OR jsonb_typeof(\"ConfigurationJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_SourceConfigurations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiPromptVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AiPromptTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: true),
                    UserPromptTemplate = table.Column<string>(type: "text", nullable: false),
                    OutputSchemaJson = table.Column<string>(type: "jsonb", nullable: true),
                    DefaultModelConfigurationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPromptVersions", x => x.Id);
                    table.CheckConstraint("CK_AiPromptVersions_OutputSchemaJson", "\"OutputSchemaJson\" IS NULL OR jsonb_typeof(\"OutputSchemaJson\") = 'object'");
                    table.CheckConstraint("CK_AiPromptVersions_VersionNumber", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_AiPromptVersions_AiModelConfigurations_DefaultModelConfigur~",
                        column: x => x.DefaultModelConfigurationId,
                        principalTable: "AiModelConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiPromptVersions_AiPromptTemplates_AiPromptTemplateId",
                        column: x => x.AiPromptTemplateId,
                        principalTable: "AiPromptTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    JobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LinkedInUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Contacts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CandidateProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    PrimaryCvDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CandidateProfiles_Documents_PrimaryCvDocumentId",
                        column: x => x.PrimaryCvDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CandidateProfiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSkills_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CandidateProfileEducations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    EducationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateProfileEducations", x => x.Id);
                    table.CheckConstraint("CK_CandidateProfileEducations_SortOrder", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_CandidateProfileEducations_CandidateProfiles_CandidateProfi~",
                        column: x => x.CandidateProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateProfileEducations_Educations_EducationId",
                        column: x => x.EducationId,
                        principalTable: "Educations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CandidateProfileExperiences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExperienceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateProfileExperiences", x => x.Id);
                    table.CheckConstraint("CK_CandidateProfileExperiences_SortOrder", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_CandidateProfileExperiences_CandidateProfiles_CandidateProf~",
                        column: x => x.CandidateProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateProfileExperiences_Experiences_ExperienceId",
                        column: x => x.ExperienceId,
                        principalTable: "Experiences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CandidateProfileProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateProfileProjects", x => x.Id);
                    table.CheckConstraint("CK_CandidateProfileProjects_SortOrder", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_CandidateProfileProjects_CandidateProfiles_CandidateProfile~",
                        column: x => x.CandidateProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateProfileProjects_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CandidateProfileSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateProfileSkills", x => x.Id);
                    table.CheckConstraint("CK_CandidateProfileSkills_SortOrder", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_CandidateProfileSkills_CandidateProfiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CandidateProfileSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PreferredCandidateProfileId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                    table.CheckConstraint("CK_Pipelines_TypeCode", "\"TypeCode\" IN ('employment', 'freelance', 'business', 'custom')");
                    table.ForeignKey(
                        name: "FK_Pipelines_CandidateProfiles_PreferredCandidateProfileId",
                        column: x => x.PreferredCandidateProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Pipelines_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TriggerTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ConditionJson = table.Column<string>(type: "jsonb", nullable: true),
                    ActionTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ActionConfigurationJson = table.Column<string>(type: "jsonb", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                    table.CheckConstraint("CK_AutomationRules_ActionConfigurationJson", "\"ActionConfigurationJson\" IS NULL OR jsonb_typeof(\"ActionConfigurationJson\") = 'object'");
                    table.CheckConstraint("CK_AutomationRules_ConditionJson", "\"ConditionJson\" IS NULL OR jsonb_typeof(\"ConditionJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_AutomationRules_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomationRules_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.CheckConstraint("CK_Campaigns_EndsAt", "\"EndsAt\" IS NULL OR \"StartsAt\" IS NULL OR \"EndsAt\" >= \"StartsAt\"");
                    table.CheckConstraint("CK_Campaigns_StatusCode", "\"StatusCode\" IN ('draft', 'active', 'paused', 'completed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_Campaigns_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Campaigns_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PipelineStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CategoryCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStages", x => x.Id);
                    table.CheckConstraint("CK_PipelineStages_CategoryCode", "\"CategoryCode\" IN ('active', 'success', 'failure')");
                    table.CheckConstraint("CK_PipelineStages_SortOrder", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_PipelineStages_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SavedSearches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SearchUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CriteriaJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSearches", x => x.Id);
                    table.CheckConstraint("CK_SavedSearches_CriteriaJson", "jsonb_typeof(\"CriteriaJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_SavedSearches_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SavedSearches_SourceConfigurations_SourceConfigurationId",
                        column: x => x.SourceConfigurationId,
                        principalTable: "SourceConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SavedSearches_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScoringRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RuleTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoringRules", x => x.Id);
                    table.CheckConstraint("CK_ScoringRules_ConfigurationJson", "jsonb_typeof(\"ConfigurationJson\") = 'object'");
                    table.CheckConstraint("CK_ScoringRules_Weight", "\"Weight\" >= -100 AND \"Weight\" <= 100");
                    table.ForeignKey(
                        name: "FK_ScoringRules_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScoringRules_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutomationExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TriggeredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationExecutions", x => x.Id);
                    table.CheckConstraint("CK_AutomationExecutions_ContextJson", "\"ContextJson\" IS NULL OR jsonb_typeof(\"ContextJson\") = 'object'");
                    table.CheckConstraint("CK_AutomationExecutions_FinishedAt", "\"FinishedAt\" IS NULL OR \"StartedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
                    table.CheckConstraint("CK_AutomationExecutions_StartedAt", "\"StartedAt\" IS NULL OR \"StartedAt\" >= \"TriggeredAt\"");
                    table.CheckConstraint("CK_AutomationExecutions_StatusCode", "\"StatusCode\" IN ('pending', 'running', 'succeeded', 'failed', 'cancelled', 'skipped')");
                    table.ForeignKey(
                        name: "FK_AutomationExecutions_AutomationRules_AutomationRuleId",
                        column: x => x.AutomationRuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomationExecutions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Opportunities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PriorityCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    ScoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunities", x => x.Id);
                    table.CheckConstraint("CK_Opportunities_PriorityCode", "\"PriorityCode\" IN ('low', 'normal', 'high')");
                    table.CheckConstraint("CK_Opportunities_Score", "\"Score\" IS NULL OR (\"Score\" >= 0 AND \"Score\" <= 100)");
                    table.ForeignKey(
                        name: "FK_Opportunities_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Opportunities_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Opportunities_PipelineStages_PipelineStageId",
                        column: x => x.PipelineStageId,
                        principalTable: "PipelineStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedSearchId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriggerTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ItemsFound = table.Column<int>(type: "integer", nullable: false),
                    ItemsCreated = table.Column<int>(type: "integer", nullable: false),
                    ItemsUpdated = table.Column<int>(type: "integer", nullable: false),
                    ItemsIgnored = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceExecutions", x => x.Id);
                    table.CheckConstraint("CK_SourceExecutions_FinishedAt", "\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
                    table.CheckConstraint("CK_SourceExecutions_ItemsCreated", "\"ItemsCreated\" >= 0");
                    table.CheckConstraint("CK_SourceExecutions_ItemsFound", "\"ItemsFound\" >= 0");
                    table.CheckConstraint("CK_SourceExecutions_ItemsIgnored", "\"ItemsIgnored\" >= 0");
                    table.CheckConstraint("CK_SourceExecutions_ItemsUpdated", "\"ItemsUpdated\" >= 0");
                    table.CheckConstraint("CK_SourceExecutions_StatusCode", "\"StatusCode\" IN ('running', 'succeeded', 'partial', 'failed', 'cancelled')");
                    table.CheckConstraint("CK_SourceExecutions_TriggerTypeCode", "\"TriggerTypeCode\" IN ('manual', 'scheduled', 'event', 'retry')");
                    table.ForeignKey(
                        name: "FK_SourceExecutions_SavedSearches_SavedSearchId",
                        column: x => x.SavedSearchId,
                        principalTable: "SavedSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceExecutions_SourceConfigurations_SourceConfigurationId",
                        column: x => x.SourceConfigurationId,
                        principalTable: "SourceConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceExecutions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelatedOpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityTypeCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventTypeCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEntries", x => x.Id);
                    table.CheckConstraint("CK_ActivityEntries_MetadataJson", "\"MetadataJson\" IS NULL OR jsonb_typeof(\"MetadataJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_ActivityEntries_Opportunities_RelatedOpportunityId",
                        column: x => x.RelatedOpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ActivityEntries_UserAccounts_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ActivityEntries_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ChannelCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CvDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoverLetterDocumentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.CheckConstraint("CK_Applications_StatusCode", "\"StatusCode\" IN ('draft', 'prepared', 'submitted', 'acknowledged', 'accepted', 'rejected', 'withdrawn')");
                    table.ForeignKey(
                        name: "FK_Applications_CandidateProfiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Applications_Documents_CoverLetterDocumentId",
                        column: x => x.CoverLetterDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Applications_Documents_CvDocumentId",
                        column: x => x.CvDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Applications_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalEventId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsAllDay = table.Column<bool>(type: "boolean", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEvents", x => x.Id);
                    table.CheckConstraint("CK_CalendarEvents_EndsAt", "\"EndsAt\" >= \"StartsAt\"");
                    table.ForeignKey(
                        name: "FK_CalendarEvents_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CalendarEvents_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CampaignOpportunities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignOpportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignOpportunities_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CampaignOpportunities_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CrmTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmTasks_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalMessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExternalThreadId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DirectionCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ToAddressesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CcAddressesJson = table.Column<string>(type: "jsonb", nullable: true),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BodyText = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMessages", x => x.Id);
                    table.CheckConstraint("CK_EmailMessages_CcAddressesJson", "\"CcAddressesJson\" IS NULL OR jsonb_typeof(\"CcAddressesJson\") = 'array'");
                    table.CheckConstraint("CK_EmailMessages_DirectionCode", "\"DirectionCode\" IN ('inbound', 'outbound')");
                    table.CheckConstraint("CK_EmailMessages_ToAddressesJson", "jsonb_typeof(\"ToAddressesJson\") = 'array'");
                    table.ForeignKey(
                        name: "FK_EmailMessages_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EmailMessages_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EmailMessages_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EmailMessages_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    RateTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proposals", x => x.Id);
                    table.CheckConstraint("CK_Proposals_Amount", "\"Amount\" IS NULL OR \"Amount\" >= 0");
                    table.CheckConstraint("CK_Proposals_AmountCurrency", "\"Amount\" IS NULL OR \"CurrencyCode\" IS NOT NULL");
                    table.CheckConstraint("CK_Proposals_CurrencyCode", "\"CurrencyCode\" IS NULL OR (char_length(\"CurrencyCode\") = 3 AND \"CurrencyCode\" ~ '^[A-Z]{3}$')");
                    table.CheckConstraint("CK_Proposals_RateTypeCode", "\"RateTypeCode\" IS NULL OR \"RateTypeCode\" IN ('fixed', 'hourly', 'daily', 'monthly', 'other')");
                    table.CheckConstraint("CK_Proposals_StatusCode", "\"StatusCode\" IN ('draft', 'sent', 'negotiating', 'accepted', 'rejected', 'expired', 'withdrawn')");
                    table.CheckConstraint("CK_Proposals_ValidUntil", "\"ValidUntil\" IS NULL OR \"SentAt\" IS NULL OR \"ValidUntil\" >= \"SentAt\"");
                    table.ForeignKey(
                        name: "FK_Proposals_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Proposals_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpportunitySources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConfigurationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SavedSearchId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunitySources", x => x.Id);
                    table.CheckConstraint("CK_OpportunitySources_LastSeenAt", "\"LastSeenAt\" IS NULL OR \"LastSeenAt\" >= \"FirstSeenAt\"");
                    table.ForeignKey(
                        name: "FK_OpportunitySources_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpportunitySources_SavedSearches_SavedSearchId",
                        column: x => x.SavedSearchId,
                        principalTable: "SavedSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunitySources_SourceConfigurations_SourceConfiguration~",
                        column: x => x.SourceConfigurationId,
                        principalTable: "SourceConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunitySources_SourceExecutions_SourceExecutionId",
                        column: x => x.SourceExecutionId,
                        principalTable: "SourceExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_ActorUserId",
                table: "ActivityEntries",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_RelatedOpportunityId",
                table: "ActivityEntries",
                column: "RelatedOpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_WorkspaceId_EntityTypeCode_EntityId_Occurre~",
                table: "ActivityEntries",
                columns: new[] { "WorkspaceId", "EntityTypeCode", "EntityId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_WorkspaceId_EventTypeCode_OccurredAt",
                table: "ActivityEntries",
                columns: new[] { "WorkspaceId", "EventTypeCode", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_WorkspaceId_OccurredAt",
                table: "ActivityEntries",
                columns: new[] { "WorkspaceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiModelConfigurations_WorkspaceId_ArchivedAt",
                table: "AiModelConfigurations",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiModelConfigurations_WorkspaceId_Enabled",
                table: "AiModelConfigurations",
                columns: new[] { "WorkspaceId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_AiModelConfigurations_WorkspaceId_ProviderCode",
                table: "AiModelConfigurations",
                columns: new[] { "WorkspaceId", "ProviderCode" });

            migrationBuilder.CreateIndex(
                name: "IX_AiModelConfigurations_WorkspaceId_ProviderCode_ModelName",
                table: "AiModelConfigurations",
                columns: new[] { "WorkspaceId", "ProviderCode", "ModelName" });

            migrationBuilder.CreateIndex(
                name: "UX_AiModelConfigurations_ActiveDefault",
                table: "AiModelConfigurations",
                column: "WorkspaceId",
                unique: true,
                filter: "\"IsDefault\" = true AND \"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_WorkspaceId_ArchivedAt",
                table: "AiPromptTemplates",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_WorkspaceId_Name",
                table: "AiPromptTemplates",
                columns: new[] { "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptTemplates_WorkspaceId_PurposeCode",
                table: "AiPromptTemplates",
                columns: new[] { "WorkspaceId", "PurposeCode" });

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptVersions_DefaultModelConfigurationId",
                table: "AiPromptVersions",
                column: "DefaultModelConfigurationId");

            migrationBuilder.CreateIndex(
                name: "UX_AiPromptVersions_Template_Version",
                table: "AiPromptVersions",
                columns: new[] { "AiPromptTemplateId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_CandidateProfileId",
                table: "Applications",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_CoverLetterDocumentId",
                table: "Applications",
                column: "CoverLetterDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_CvDocumentId",
                table: "Applications",
                column: "CvDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_OpportunityId",
                table: "Applications",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_Applications_OpportunityId_StatusCode",
                table: "Applications",
                columns: new[] { "OpportunityId", "StatusCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_SubmittedAt",
                table: "Applications",
                column: "SubmittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_AutomationRuleId_TriggeredAt",
                table: "AutomationExecutions",
                columns: new[] { "AutomationRuleId", "TriggeredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_StatusCode_TriggeredAt",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "StatusCode", "TriggeredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_TriggeredAt",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "TriggeredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_PipelineId",
                table: "AutomationRules",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_PipelineId_Enabled",
                table: "AutomationRules",
                columns: new[] { "PipelineId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_WorkspaceId_ArchivedAt",
                table: "AutomationRules",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_WorkspaceId_Enabled",
                table: "AutomationRules",
                columns: new[] { "WorkspaceId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_WorkspaceId_TriggerTypeCode",
                table: "AutomationRules",
                columns: new[] { "WorkspaceId", "TriggerTypeCode" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_ContactId",
                table: "CalendarEvents",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_OpportunityId",
                table: "CalendarEvents",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_WorkspaceId_StartsAt",
                table: "CalendarEvents",
                columns: new[] { "WorkspaceId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "UX_CalendarEvents_Workspace_Provider_ExternalEvent",
                table: "CalendarEvents",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalEventId" },
                unique: true,
                filter: "\"ExternalEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignOpportunities_OpportunityId",
                table: "CampaignOpportunities",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "UX_CampaignOpportunities_Campaign_Opportunity",
                table: "CampaignOpportunities",
                columns: new[] { "CampaignId", "OpportunityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_PipelineId",
                table: "Campaigns",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_WorkspaceId_ArchivedAt",
                table: "Campaigns",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_WorkspaceId_StatusCode",
                table: "Campaigns",
                columns: new[] { "WorkspaceId", "StatusCode" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileEducations_CandidateProfileId_SortOrder",
                table: "CandidateProfileEducations",
                columns: new[] { "CandidateProfileId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileEducations_EducationId",
                table: "CandidateProfileEducations",
                column: "EducationId");

            migrationBuilder.CreateIndex(
                name: "UX_ProfileEducations_Profile_Target",
                table: "CandidateProfileEducations",
                columns: new[] { "CandidateProfileId", "EducationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileExperiences_CandidateProfileId_SortOrder",
                table: "CandidateProfileExperiences",
                columns: new[] { "CandidateProfileId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileExperiences_ExperienceId",
                table: "CandidateProfileExperiences",
                column: "ExperienceId");

            migrationBuilder.CreateIndex(
                name: "UX_ProfileExperiences_Profile_Target",
                table: "CandidateProfileExperiences",
                columns: new[] { "CandidateProfileId", "ExperienceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileProjects_CandidateProfileId_SortOrder",
                table: "CandidateProfileProjects",
                columns: new[] { "CandidateProfileId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileProjects_ProjectId",
                table: "CandidateProfileProjects",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "UX_ProfileProjects_Profile_Target",
                table: "CandidateProfileProjects",
                columns: new[] { "CandidateProfileId", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfiles_PrimaryCvDocumentId",
                table: "CandidateProfiles",
                column: "PrimaryCvDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfiles_WorkspaceId_ArchivedAt",
                table: "CandidateProfiles",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfiles_WorkspaceId_Name",
                table: "CandidateProfiles",
                columns: new[] { "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "UX_CandidateProfiles_ActiveDefault",
                table: "CandidateProfiles",
                column: "WorkspaceId",
                unique: true,
                filter: "\"IsDefault\" = true AND \"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileSkills_CandidateProfileId_SortOrder",
                table: "CandidateProfileSkills",
                columns: new[] { "CandidateProfileId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfileSkills_SkillId",
                table: "CandidateProfileSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "UX_ProfileSkills_Profile_Target",
                table: "CandidateProfileSkills",
                columns: new[] { "CandidateProfileId", "SkillId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_WorkspaceId_Name",
                table: "Companies",
                columns: new[] { "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_CompanyId",
                table: "Contacts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_WorkspaceId_CompanyId",
                table: "Contacts",
                columns: new[] { "WorkspaceId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_WorkspaceId_Email",
                table: "Contacts",
                columns: new[] { "WorkspaceId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_IsCompleted_DueAt",
                table: "CrmTasks",
                columns: new[] { "IsCompleted", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_OpportunityId",
                table: "CrmTasks",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_WorkspaceId_ArchivedAt",
                table: "Documents",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_WorkspaceId_KindCode",
                table: "Documents",
                columns: new[] { "WorkspaceId", "KindCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_WorkspaceId_Sha256",
                table: "Documents",
                columns: new[] { "WorkspaceId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "UX_Documents_Workspace_StorageKey",
                table: "Documents",
                columns: new[] { "WorkspaceId", "StorageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Educations_WorkspaceId",
                table: "Educations",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_CompanyId",
                table: "EmailMessages",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_ContactId",
                table: "EmailMessages",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_OpportunityId",
                table: "EmailMessages",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_WorkspaceId_DirectionCode_OccurredAt",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "DirectionCode", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_WorkspaceId_OccurredAt",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_EmailMessages_Workspace_Provider_ExternalMessage",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalMessageId" },
                unique: true,
                filter: "\"ExternalMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Experiences_WorkspaceId",
                table: "Experiences",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_CompanyId",
                table: "Opportunities",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_ContactId",
                table: "Opportunities",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_PipelineStageId",
                table: "Opportunities",
                column: "PipelineStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_WorkspaceId_ArchivedAt",
                table: "Opportunities",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_WorkspaceId_CompanyId",
                table: "Opportunities",
                columns: new[] { "WorkspaceId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_WorkspaceId_ContactId",
                table: "Opportunities",
                columns: new[] { "WorkspaceId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_WorkspaceId_PipelineStageId",
                table: "Opportunities",
                columns: new[] { "WorkspaceId", "PipelineStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_OpportunityId",
                table: "OpportunitySources",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_SavedSearchId",
                table: "OpportunitySources",
                column: "SavedSearchId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_SourceConfigurationId",
                table: "OpportunitySources",
                column: "SourceConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_SourceExecutionId",
                table: "OpportunitySources",
                column: "SourceExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_SourceUrl",
                table: "OpportunitySources",
                column: "SourceUrl");

            migrationBuilder.CreateIndex(
                name: "UX_OpportunitySources_Opportunity_SourceUrl",
                table: "OpportunitySources",
                columns: new[] { "OpportunityId", "SourceUrl" },
                unique: true,
                filter: "\"SourceUrl\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_OpportunitySources_SourceConfiguration_ExternalId",
                table: "OpportunitySources",
                columns: new[] { "SourceConfigurationId", "ExternalId" },
                unique: true,
                filter: "\"SourceConfigurationId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_PreferredCandidateProfileId",
                table: "Pipelines",
                column: "PreferredCandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_WorkspaceId_ArchivedAt",
                table: "Pipelines",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_PipelineStages_Pipeline_SortOrder",
                table: "PipelineStages",
                columns: new[] { "PipelineId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId_ArchivedAt",
                table: "Projects",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId_Name",
                table: "Projects",
                columns: new[] { "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSkills_SkillId",
                table: "ProjectSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectSkills_Project_Skill",
                table: "ProjectSkills",
                columns: new[] { "ProjectId", "SkillId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_DocumentId",
                table: "Proposals",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_OpportunityId",
                table: "Proposals",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_OpportunityId_StatusCode",
                table: "Proposals",
                columns: new[] { "OpportunityId", "StatusCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_SentAt",
                table: "Proposals",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_PipelineId",
                table: "SavedSearches",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_SourceConfigurationId",
                table: "SavedSearches",
                column: "SourceConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_WorkspaceId_ArchivedAt",
                table: "SavedSearches",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_PipelineId",
                table: "ScoringRules",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_PipelineId_Enabled",
                table: "ScoringRules",
                columns: new[] { "PipelineId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_WorkspaceId_ArchivedAt",
                table: "ScoringRules",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRules_WorkspaceId_Enabled",
                table: "ScoringRules",
                columns: new[] { "WorkspaceId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_Skills_WorkspaceId_CategoryCode",
                table: "Skills",
                columns: new[] { "WorkspaceId", "CategoryCode" });

            migrationBuilder.CreateIndex(
                name: "UX_Skills_Workspace_Name",
                table: "Skills",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceConfigurations_WorkspaceId_ArchivedAt",
                table: "SourceConfigurations",
                columns: new[] { "WorkspaceId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceConfigurations_WorkspaceId_SourceTypeCode",
                table: "SourceConfigurations",
                columns: new[] { "WorkspaceId", "SourceTypeCode" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutions_SavedSearchId_StartedAt",
                table: "SourceExecutions",
                columns: new[] { "SavedSearchId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutions_SourceConfigurationId_StartedAt",
                table: "SourceExecutions",
                columns: new[] { "SourceConfigurationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutions_WorkspaceId_StartedAt",
                table: "SourceExecutions",
                columns: new[] { "WorkspaceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutions_WorkspaceId_StatusCode_StartedAt",
                table: "SourceExecutions",
                columns: new[] { "WorkspaceId", "StatusCode", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_UserAccounts_Email",
                table: "UserAccounts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_ArchivedAt",
                table: "Workspaces",
                column: "ArchivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_OwnerUserId",
                table: "Workspaces",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEntries");

            migrationBuilder.DropTable(
                name: "AiPromptVersions");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "AutomationExecutions");

            migrationBuilder.DropTable(
                name: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "CampaignOpportunities");

            migrationBuilder.DropTable(
                name: "CandidateProfileEducations");

            migrationBuilder.DropTable(
                name: "CandidateProfileExperiences");

            migrationBuilder.DropTable(
                name: "CandidateProfileProjects");

            migrationBuilder.DropTable(
                name: "CandidateProfileSkills");

            migrationBuilder.DropTable(
                name: "CrmTasks");

            migrationBuilder.DropTable(
                name: "EmailMessages");

            migrationBuilder.DropTable(
                name: "OpportunitySources");

            migrationBuilder.DropTable(
                name: "ProjectSkills");

            migrationBuilder.DropTable(
                name: "Proposals");

            migrationBuilder.DropTable(
                name: "ScoringRules");

            migrationBuilder.DropTable(
                name: "AiModelConfigurations");

            migrationBuilder.DropTable(
                name: "AiPromptTemplates");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropTable(
                name: "Campaigns");

            migrationBuilder.DropTable(
                name: "Educations");

            migrationBuilder.DropTable(
                name: "Experiences");

            migrationBuilder.DropTable(
                name: "SourceExecutions");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropTable(
                name: "Opportunities");

            migrationBuilder.DropTable(
                name: "SavedSearches");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "PipelineStages");

            migrationBuilder.DropTable(
                name: "SourceConfigurations");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropTable(
                name: "Pipelines");

            migrationBuilder.DropTable(
                name: "CandidateProfiles");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "UserAccounts");
        }
    }
}
