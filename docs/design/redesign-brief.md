# UI/UX redesign brief (standing spec)

Status: **agreed brief**, given by Philip on 23/10/2026. Supersedes the open `[Philip]` items in `docs/ui-ux-redesign-questionnaire.md` wherever the two overlap. Tooling: `docs/ui-ux-redesign-toolchain.md`. Discovery and audit: `docs/design/redesign-discovery.md`.

This file condenses Philip's brief without changing its meaning. Where wording matters it is quoted.

## Role and objective

Lead product designer, design-systems architect, accessibility specialist and Blazor front-end engineer for a comprehensive redesign. Authority to redesign presentation, navigation, information architecture and workflows where that materially improves usability. "This is not a superficial CSS reskin." Turn a functionally mature but bland application into "a vibrant, intuitive, exciting, colourful and inspiring Orthodox Christian educational experience" while keeping or improving its functional requirements. Subagents and other models may be used; design coherence stays with the lead.

## First principle

**"Orthodox first. Varangian second."** The purpose is to catechise young Orthodox Christians. The Varangian history gives identity, narrative, metaphor and belonging, and must never overwhelm or trivialise the Orthodox purpose. "Do not create a Viking-themed LMS with Christian decoration."

## Identity

- Based in England. After the Norman Conquest, Anglo-Saxon nobles went to the Eastern Roman Empire and were strongly represented in the Varangian Guard, which defended the Emperor, understood as defender of the Orthodox Faith.
- Metaphor: children grow in knowledge, character and faith to become confident representatives and defenders of their Orthodox faith. Not militarism.
- Themes: service, faithfulness, responsibility, courage, belonging, formation, progression, guardianship, Christian maturity.

## Users and devices

- Students about **8 to 18**. No fixed age-to-rank mapping; Teachers assign ranks. **One coherent student design system** across the age range, no childish and teenage variants.
- Roles: Student, Parent, Teacher, Curriculum Approver, Safeguarding Officer, Administrator; combinable on one account.
- Phones, tablets, laptops and desktops for every role. Do not assume a role implies a device. Responsive design is fundamental.

## Constraints

- .NET 10 Blazor Web App, Interactive Server, Razor components, ASP.NET Core Identity, EF Core, PostgreSQL, NuGet only, no JS framework, minimal JavaScript, PWA. Do not replace the architecture to ease front-end work. Use the approved UI/UX tooling.
- Routes normally stay stable (tested, linked from emails).
- Workflows may change where they produce poor UX, but safeguarding, data protection, authorisation, sensitive-data exposure, auditing, security and approval boundaries must be preserved. Understand why a safeguard exists before changing a related workflow. Any functional change is deliberate, justified and tested.

## Progression

- Ten ranks: Recruit, Guardsman, Shield-bearer, Sword-bearer, Herald, Torch-bearer, Standard-bearer, Cross-bearer, Christ-bearer, Akolouthos. Lessons per rank, automatic promotion, rank badges, certificates.
- **Do not introduce** XP, points, streaks, leaderboards, artificial currencies, competitive rankings or generic game mechanics.
- Make the existing progression substantially more engaging: "A student should feel that he or she is **becoming a Varangian**." Significant, exciting, meaningful, without turning catechesis into a game. The rank journey can be the visual and narrative spine.

## Visual character

- Vibrant, intuitive, exciting, colourful, inspiring; also reverent, credible, accessible, usable and recognisably Orthodox. Avoid sombre, brown, beige, archaic or lifeless "religious software".
- Hierarchy of influences: **Orthodox Christianity** primary (immediately apparent); **Varangian history** secondary (character, narrative, progression); an artistic **synthesis of Byzantine/Eastern Roman and Anglo-Saxon** traditions (ornament, manuscript illumination, interlace, geometric borders, ecclesiastical architecture, mosaics, crosses, standards, shields, torches, Eastern Roman motifs).
- Avoid generic Scandinavian Viking imagery, Viking games, fantasy RPGs, Marvel-style warriors, faux-medieval gaming interfaces, Norse runes, grimdark, militaristic propaganda.
- Branding: dark blue and gold are historical context, **not constraints**. Build the colour system from first principles; keep blue and gold only if justified. The palette must suit vibrant child-facing screens, restrained staff screens and data-heavy screens, give clear semantic state colours, and meet WCAG 2.2 AA contrast.
- **Double-headed eagle:** an existing motif with no suitable asset. Do not copy an arbitrary internet image as the logo. Treat it as inspiration within a broader identity, not forced onto every screen.

## Sacred imagery

**"The Way" (Philip, 23/10/2026):** do not use "The Way" as a label for anything in the app: it is the Orthodox name for the faith, the Way of Life of the Didache, and a self-epithet of Christ. Avoid "religion" too.

**Terminology (Philip, 23/10/2026):** an icon is a specific liturgical art form, written (not painted) in a prescribed way and venerated. A picture of Christ or a saint is not automatically an icon. The Christ-bearer patch shows Christ and is a badge, not an icon: a badge is never venerated. Existing non-icon images of Christ, such as Orthodox Arts Journal drawings, may be used if coloured. The rules below about icons apply to liturgical icons.

**The double-headed eagle (Philip, 23/10/2026)** is the emblem of the Eastern Orthodox Church and is to feature in the interface. The group's existing logo (a cross with IC XC NI KA and "Varangians", `seed/admin/GraphicDesign/Logo*.png`) is the product mark.

- Authentic Orthodox icons may appear where contextually appropriate, not only in lesson content.
- **"Never generate pseudo-Orthodox sacred iconography using AI"**: no generated Christ, Theotokos, saints, angels or sacred scenes. Source authentic Orthodox iconography.
- Icons never become reward badges, buttons, loading indicators, achievement graphics or decorative gimmicks. Treat with reverence.
- All theological and liturgical influences authentically **Eastern Orthodox**; no casual Roman Catholic, Protestant, generic Christian or invented imagery.
- **Orthodox Illustration Project** (https://orthodoxartsjournal.org/orthodox-illustration-project/): major inspiration and, where its terms permit, a source of assets. Check licensing and attribution per asset. Study its line drawing, ornament, borders, headers, churches, liturgical scenes, small motifs and typography; inform the design system rather than pasting artwork. Clarified 23/10/2026 (Philip): primarily a source of inspiration. Its assets may be used only within the journal's published terms; no confirmation will be sought and deployment does not wait on one.
- Generative tools may be used for **non-sacred** artwork only (abstract ornament, borders, textures, shields, standards, architectural decoration, fusion patterns, backgrounds, non-sacred historical illustration), supporting the design rather than overwhelming it.

## References

Study Khan Academy and BBC Bitesize for clarity, accessibility, navigation, hierarchy, progression, content discovery, approachability, responsiveness and making education inviting. Do not imitate their branding.

## Experiences by audience

- **Student:** rich, vibrant, immersive. Make clear: current rank, completed lessons, remaining progress, previous and future ranks, badges, certificates, what comes next. Create anticipation. Explore (not assume) a visual rank journey. The metaphor may extend beyond rank names where it helps, never gimmicky.
- **Parent:** warm, clear, reassuring.
- **Teacher:** attractive but task-focused.
- **Safeguarding/Admin:** restrained, highly usable, professional. "A safeguarding officer reviewing consent information does not need a theatrical Varangian experience."
- All unmistakably the same application.

## Real-world catechesis

A session is games together, a snack, then about 45 minutes of catechetical teaching. The application supports the teacher; it does not replace them. Model: **teacher-led formation in person plus digital support and continued access at home.** Not self-directed e-learning.

## Lessons and resources (major roadmap priority)

- Lesson material is mostly Word documents and associated files today; it should become first-class in-app content (format not predetermined): structured text, Scripture, quotations, authentic iconography, photographs, illustrations, handouts, printable presentation.
- Teachers propose changes, edit or provide content, and submit for approval; approval must remain before anything is published.
- Do not necessarily build the full authoring architecture in the visual redesign, but **do not design as if Word downloads are the permanent content model.**

## Accessibility

WCAG 2.2 AA mandatory and designed in: contrast, keyboard, focus, semantic HTML, labels, validation, accessible names, target sizes, reflow, zoom, readable type, reduced motion, screen-reader structure. Ornament never obscures hierarchy, text or controls.

## Design system first

Before page redesign, define: palette, semantic colours, typography, type scale, spacing, breakpoints, surfaces, borders, radius, shadows, buttons, links, inputs, selects, checkboxes, radios, validation, cards, navigation, tables, responsive data, alerts, notices, status indicators, progress indicators, badges, rank presentation, empty/loading/error states, dialogues, imagery, iconography, ornament, motion. Reusable tokens and components over page-specific styling.

## Phases

1. **Discovery:** repository, journeys, page and component inventory, run the app, inspect every role at mobile, tablet and desktop; usability, visual, accessibility problems; simplification; duplicated patterns; shared primitives. Browser tooling, not source alone.
2. **UX and visual audit**, prioritised by impact.
3. **Design direction:** concept; Orthodox, Anglo-Saxon and Byzantine influence; Varangian metaphor; colour, type, ornament, imagery rationale; student progression concept. No application-wide implementation until coherent.
4. **Design system** implemented in the app.
5. **Two working prototypes in the running app:** `/student` (expressive, rank and progression) and `/progress/record` (practical staff workflow). Test at mobile, tablet, desktop; critique and iterate.
6. **Mandatory approval gate: STOP.** Present direction, changes, rationale, screenshots or browser-accessible builds, viewport results, accessibility findings, workflow changes, questions. Wait for Philip's approval.
7. Only after approval: application-wide implementation, systematic, with continuous visual QA (screenshots at each viewport; spacing, wrapping, overflow, hierarchy, balance, mobile failures, excess ornament, contrast, inconsistency).

## Discipline

- Dedicated branch/worktree. Follow `CLAUDE.md` and `STATE.md`. Do not weaken tests; update assertions deliberately; never delete failing tests. `pwsh -File build/verify.ps1` must pass for a phase to be complete. Update ASVS and WCAG documentation for changed pages.
- Security and privacy are architectural: never expose sensitive data to simplify navigation; never move it into generic dashboards or client-visible components without understanding the authorisation and audit model.

## Quality bar

"Not 'good for a church website'": a genuinely excellent modern educational web application with an identity that could belong only to Varangians. Avoid generic Bootstrap, generic SaaS dashboards, gradient-heavy AI landing pages, glassmorphism, arbitrary cards everywhere, "medieval" fonts, fake parchment or stone, Viking clichés, excess ornament, historical typefaces for body text, superficial crosses. The Orthodox and Anglo-Saxon influences shape the **design language**, not merely decoration.

## Autonomy

Use judgement; do not ask about minor details a senior designer should resolve; research; use subagents where useful; challenge poor UX; make and explain strong decisions. Observe the approval gate.
