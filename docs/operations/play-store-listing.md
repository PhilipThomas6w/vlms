# Google Play listing and app content (VLMS-PLAY)

Draft answers for Play Console, for Philip to paste (drafted 07/11/2026). Every fact here comes from the privacy notice (`src/Vlms.Web/Components/Pages/Privacy.razor`) or the requirements (`requirements/functional.md`). If the notice changes, this page and the Data safety form change with it. Items marked **Decision** need Philip's answer before submission.

## 1. Main store listing

**App name** (30 characters max): `Varangians`

**Short description** (80 characters max, 77 used):

```
Lessons, ranks and consent for chapters of the Varangians Orthodox youth club
```

**Full description** (4,000 characters max):

```
Varangians is the app for chapters of the Varangians Orthodox youth club. Each chapter is run by its parish, and the app is free.

Students
• Read the lessons for your rank, on your phone
• See your rank, your progress, your badges and your certificates

Parents and guardians
• Apply for your child to join a chapter
• Complete the annual consent form
• Follow your child's progress through the ranks
• Create and reset your child's sign-in

Teachers
• Browse every lesson in every rank
• Record lesson completions and take the attendance register
• See each student's rank and progress
• Suggest changes to lessons

Chapter administrators and safeguarding officers
• Review applications from parents
• Keep consent and DBS records, with reminders before they expire

Everyone
• A calendar with the Church's liturgical calendar and chapter events
• Save lessons to read without a connection

You need an account from your chapter to sign in. Information is visible only to the people whose role needs it, and staff who can see sensitive information use two-factor authentication. The app has no advertising, analytics or tracking.
```

**Graphics** (Philip to supply; I can produce the icon and feature graphic from `icons/icon.svg` if wanted):
- App icon: 512 × 512 PNG, 32-bit.
- Feature graphic: 1024 × 500 PNG or JPEG.
- Phone screenshots: at least 2, 16:9 or 9:16, each side 320–3840 px. Use screenshots that show no real child's details.

**Category:** App → Education. **Tags:** choose from Google's list (Education, Religion if offered).

**Contact details:** email `admin@varangians.co.uk`; website `https://www.varangians.co.uk`. Phone: leave blank.

## 2. App content

| Section | Answer |
|---|---|
| Privacy policy | `https://www.varangians.co.uk/privacy` |
| Ads | No, the app does not contain ads |
| App access | All or some functionality is restricted: see §3 |
| Content rating | See §4 |
| Target audience and content | See §5 |
| Data safety | See §6 |
| Government apps | No |
| Financial features | None |
| Health | The app does not have health features (it records emergency medical details for safeguarding; it is not a health app) |
| News app | No |
| Account deletion | Users can create an account in the app: Yes. Deletion URL: `https://www.varangians.co.uk/privacy#delete-account` |

## 3. App access (reviewer sign-in)

Google's reviewers need a sign-in that works without two-factor authentication.

**Decision (draft):** create a reviewer account that holds only the Parent role in the Shrewsbury chapter and is linked to no child. It sees the lesson library, calendar and parent pages without any child's data. Steps: register at `/Account/Register` with an address you control (e.g. a `+playreview` alias of your own email), confirm it, then grant Parent at **Administration → User roles**. Before submitting, sign in as it and check that it sees no child's details.

Instructions for Google (paste into App access):

```
Sign in with the email address and password below. The account is a parent account with no children linked, so child records are empty by design. The lesson library and calendar are available from the menu.
```

## 4. Content rating questionnaire

- Email: `admin@varangians.co.uk`. Category: the educational/reference category (wording varies; not a game, not social media).
- Violence, fear, sexual content, crude humour, bad language, drugs, alcohol, tobacco, gambling, simulated gambling: **No** to all.
- Users interact or exchange content: **Yes**. Parents and chapter staff exchange messages within an application review, and teachers send lesson suggestions to the curriculum approver. There is no open chat, and nothing is public. Answering No would be inaccurate.
- Shares the user's location: **No**. Digital purchases: **No**.

## 5. Target audience and content

Recorded earlier (`governance/privacy-notice.md`): the target audience includes children under 13, so the Google Play Families policy applies. Children use the app through student sign-ins (`adr/0007`).

**Decision:** the age groups to tick. Google offers 5 and under, 6–8, 9–12, 13–15, 16–17 and 18+. Tick every group that contains students, plus 18+ for parents and staff. Philip to give the student age range; it is not recorded in `docs/`.

- Appeals to children: Yes (it is a youth club app).
- Ads: none. Families policy fits: no ads, no third-party SDKs, no analytics, sign-in only with an account the chapter approves.
- Teacher Approved / Families programme opt-in: not required; skip.

## 6. Data safety

General answers:
- Does the app collect or share any of the required user data types? **Yes.**
- Is all user data encrypted in transit? **Yes** (HTTPS only).
- Can users request that their data be deleted? **Yes** (email request, `/privacy#delete-account`).

**Shared:** **No** for every type. The hosting, backup and email providers are service providers, which Google exempts. Sharing a child's name, rank and progress with the Archdiocese office happens only with the parent's explicit consent on the form, which Google also exempts ([Data safety help](https://support.google.com/googleplay/android-developer/answer/10787469)). Disclosure to police or children's services for legal or safeguarding reasons is the legal-purposes exemption.

**Collected** (not processed ephemerally; no type is used for advertising, analytics or personalisation):

| Category → type | Collected | Required or optional | Purposes |
|---|---|---|---|
| Personal info → Name | Yes | Required | App functionality, Account management |
| Personal info → Email address | Yes | Required (adult accounts) | App functionality, Account management, Developer communications |
| Personal info → User IDs | Yes (account ID, student username) | Required | App functionality, Account management |
| Personal info → Address | Yes (parent application) | Required | App functionality |
| Personal info → Phone number | Yes (parent application, emergency contacts) | Required | App functionality |
| Personal info → Other info | Yes (child's date of birth; DBS status and certificate number for staff) | Required | App functionality, Fraud prevention, security and compliance |
| Personal info → Political or religious beliefs | **Decision**, draft: No. No field records religion. The notice treats membership as possibly showing religious belief, but Google's form asks about data collected | — | — |
| Health and fitness → Health info | Yes (emergency medical, dietary and special educational needs details) | Optional | App functionality |
| App activity → Other user-generated content | Yes (lesson suggestions and review comments, application messages) | Optional | App functionality |
| App activity → Other actions | Yes (lesson progress, attendance, badges, certificates) | Required | App functionality |
| App info and performance → Diagnostics | Yes (server request logs with IP address and browser details, kept up to 90 days) | Required | Fraud prevention, security and compliance |

**Not collected:** location (IP is not used to infer location), financial info, messages (email, SMS), photos and videos (the app stores photo consent, not photos), audio, files and docs, calendar, contacts, web browsing, in-app search history, installed apps, crash logs, device or other IDs.

## 7. Closed test and production

A personal account created after 13/11/2023 needs a closed test with **12 or more testers opted in for 14 days in a row** before production access. Testers give their Google account emails; add them to a list on **Closed testing → Testers**, and promote the internal release (or upload a new bundle with a higher version code, `release.md` §7).
