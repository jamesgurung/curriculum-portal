# Curriculum Portal

Curriculum Portal is a free, open-source web application designed to help schools design their schemes and assessments.

Deploy effortlessly to Microsoft Azure.

### Setup

1. Create a general purpose v2 storage account in [Microsoft Azure](http://portal.azure.com), and within it create:
    * Blob containers: `config` and `curriculum`
    * Tables: `courses` and `units`

2. Within the `config` blob container:

    * Upload a blank file `keys.xml`. Generate a SAS URL for this file with read/write permissions and a distant expiry. This will be used to store the application's data protection keys so that auth cookies persist across app restarts.

    * Upload a file `students.csv` with the following headers and populate it with all students in your school. "Id" can be any unique integer identifier. The "Classes" field should contain a comma-separated list of classes, wrapped in double quotes. To correctly represent accented characters in student names, save the file in 'CSV UTF-8' format.

        ```csv
        Id,Email,FirstName,LastName,TutorGroup,Classes
        ```

    * Upload a file `teachers.csv` with the same format.

    * Upload a file `holidays.csv` with the following headers and dates in `yyyy-MM-dd` format. If an assignment due date lands within a holiday range, it will be pushed forward.

        ```csv
        Start,End
        ```

    * If you are using Class Charts to record behaviour events, upload `classcharts-behaviours.json` containing the behaviour settings for `positive` and `negative`.

        ```json
        {
          "positive": {
            "id": 123456,
            "reason": "Revision Quizzes Completed",
            "score": 10,
            "icon": "good/+thinking_skills.png"
          },
          "negative": {
            "id": 123457,
            "reason": "Insufficient Completion",
            "score": -6,
            "icon": "bad/-lack_of_books.png"
          }
        }
        ```

    * Upload `checklist.json` containing the checklist items to show for each unit. Each `id` must be unique and may only contain letters, numbers, hyphens, and underscores.

        ```json
        [
          {
            "id": "participation",
            "title": "Every lesson includes high-participation activities throughout"
          },
          {
            "id": "vocabulary",
            "title": "Tier 3 vocabulary is identified and explicitly taught"
          }
        ]
        ```

    * Upload `school-logo.png`.

3. If you are using [Microsoft Foundry](https://ai.azure.com/), create a project and deploy an OpenAI reasoning model (e.g. `gpt-5.4`). Set `MicrosoftFoundryEndpoint` to use Microsoft Foundry. If `MicrosoftFoundryEndpoint` is not set, the app uses the direct OpenAI API instead.

4. Create an Azure app registration.
    * Name - `Curriculum Portal`
    * Redirect URI - `https://<app-website-domain>/signin-oidc`
    * Implicit grant - ID tokens
    * Supported account types - Accounts in this organizational directory only
    * API permissions - `Microsoft Graph - User.Read`
    * Token configuration - add an optional claim of type ID: `upn`
    * Certificates & secrets - create a new client secret

5. Create an Azure App Service web app.
    * Publish mode - Container
    * Operating system - Linux
    * Image source - Other container registries
    * Container name - `main`
    * Access type - Public
    * Registry server URL - `https://index.docker.io`
    * Image and tag - `jamesgurung/curriculum-portal:latest`
    * Port - 8080
    * Startup command: (blank)

6. Configure the following environment variables for the web app:

    * `AdminEmails__0` - the email address of the first admin user, who has full administrative access (subsequent admins can be configured by adding items with incrementing indices)
    * `AssignmentCompletionHighThreshold` - the integer percentage completion rate above which students due assignments today receive the positive Class Charts behaviour (set this above `100` to disable positive behaviours)
    * `AssignmentCompletionLowThreshold` - the integer percentage completion rate below which students due assignments today receive the negative Class Charts behaviour (set this below `0` to disable negative behaviours)
    * `ClassChartsEmail` - the email address of the Class Charts account used to issue behaviours (if you are using Class Charts to record behaviour events)
    * `ClassChartsPassword` - the password for the Class Charts account used to issue behaviours (if you are using Class Charts to record behaviour events)
    * `DataControllerName` - the name of the organisation acting as data controller for the privacy page
    * `DataProtectionBlobUri` - the SAS URL for the keys file you created earlier
    * `MicrosoftClientId` - the client ID of your Azure app registration
    * `MicrosoftClientSecret` - the client secret of your Azure app registration
    * `MicrosoftFoundryEndpoint` - the endpoint URL for your Microsoft Foundry deployment, e.g. `https://<project>.cognitiveservices.azure.com/` (optional; if set, this is used instead of the OpenAI API)
    * `MicrosoftSharePointSubdomain` - your Microsoft 365 SharePoint subdomain (for example `contoso` in `https://contoso.sharepoint.com`)
    * `MicrosoftTeamsPrefix` - the `externalName` prefix used to identify the relevant Microsoft Teams classes when publishing assignment tasks. Teams assignments are published to KS3 tutor groups only for year groups where at least one assignment is set.
    * `MicrosoftTenantId` - your Azure tenant ID
    * `OpenAIApiKey` - your OpenAI or Microsoft Foundry API key
    * `OpenAIModel` - the OpenAI model name or Microsoft Foundry deployment name
    * `PrivacyNoticeUrl` - the absolute URL of the school's official privacy notice
    * `SchoolName` - the name of your school
    * `StorageAccountConnectionString` - the connection string for the Azure Storage account
    * `SyncApiKey` - the secret key to use if you update the `students.csv` and `teachers.csv` files with an automated script (optional)
    * `Website` - the public base URL of your deployed Curriculum Portal, e.g. `https://example.com`

### Contributing

If you have a question or feature request, please open an issue.

To contribute improvements to this project, or to adapt the code for the specific needs of your school, you are welcome to fork the repository.

Pull requests are welcome; please open an issue first to discuss.
