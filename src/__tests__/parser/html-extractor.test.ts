import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { HTMLExtractor } from '../../extractors/html-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('HTMLExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Basic HTML Structure and Semantic Elements', () => {
    it('should extract document structure, semantic elements, and attributes', async () => {
      const htmlCode = `
<!DOCTYPE html>
<html lang="en" data-theme="light">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta name="description" content="A comprehensive web application for project management">
  <meta name="keywords" content="project, management, productivity, collaboration">
  <meta name="author" content="Development Team">
  <meta property="og:title" content="Project Manager Pro">
  <meta property="og:description" content="Streamline your project workflow">
  <meta property="og:image" content="/images/og-image.jpg">
  <meta property="og:url" content="https://projectmanager.example.com">
  <meta name="twitter:card" content="summary_large_image">

  <title>Project Manager Pro - Streamline Your Workflow</title>

  <link rel="stylesheet" href="/css/main.css">
  <link rel="stylesheet" href="/css/themes.css">
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preload" href="/fonts/inter.woff2" as="font" type="font/woff2" crossorigin>
  <link rel="icon" type="image/svg+xml" href="/favicon.svg">
  <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">
  <link rel="manifest" href="/site.webmanifest">

  <script type="application/ld+json">
  {
    "@context": "https://schema.org",
    "@type": "WebApplication",
    "name": "Project Manager Pro",
    "description": "A comprehensive project management tool",
    "url": "https://projectmanager.example.com",
    "applicationCategory": "ProductivityApplication",
    "operatingSystem": "Web Browser"
  }
  </script>
</head>

<body class="theme-light" data-env="production">
  <div id="app" class="app-container">
    <!-- Skip to main content for accessibility -->
    <a href="#main-content" class="skip-link">Skip to main content</a>

    <!-- Header with navigation -->
    <header class="header" role="banner">
      <div class="container">
        <div class="header-content">
          <a href="/" class="logo" aria-label="Project Manager Pro Homepage">
            <img src="/images/logo.svg" alt="Project Manager Pro" width="120" height="40">
          </a>

          <nav class="navigation" role="navigation" aria-label="Main navigation">
            <ul class="nav-list">
              <li class="nav-item">
                <a href="/dashboard" class="nav-link" aria-current="page">Dashboard</a>
              </li>
              <li class="nav-item">
                <a href="/projects" class="nav-link">Projects</a>
              </li>
              <li class="nav-item">
                <a href="/team" class="nav-link">Team</a>
              </li>
              <li class="nav-item">
                <a href="/analytics" class="nav-link">Analytics</a>
              </li>
            </ul>
          </nav>

          <div class="header-actions">
            <button type="button" class="btn btn-icon" aria-label="Toggle theme" data-action="toggle-theme">
              <span class="icon-theme" aria-hidden="true"></span>
            </button>

            <button type="button" class="btn btn-icon" aria-label="Notifications" data-badge="3">
              <span class="icon-bell" aria-hidden="true"></span>
            </button>

            <div class="user-menu" data-component="dropdown">
              <button type="button" class="user-avatar" aria-label="User menu" aria-expanded="false" aria-haspopup="true">
                <img src="/images/avatars/user-123.jpg" alt="John Doe" width="32" height="32">
              </button>

              <div class="dropdown-menu" role="menu" aria-hidden="true">
                <a href="/profile" class="dropdown-item" role="menuitem">Profile</a>
                <a href="/settings" class="dropdown-item" role="menuitem">Settings</a>
                <hr class="dropdown-divider">
                <button type="button" class="dropdown-item" role="menuitem" data-action="logout">
                  Sign Out
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </header>

    <!-- Main content area -->
    <main id="main-content" class="main" role="main">
      <div class="container">
        <!-- Page header with breadcrumbs -->
        <div class="page-header">
          <nav aria-label="Breadcrumb">
            <ol class="breadcrumb">
              <li class="breadcrumb-item">
                <a href="/dashboard">Dashboard</a>
              </li>
              <li class="breadcrumb-item active" aria-current="page">
                Projects
              </li>
            </ol>
          </nav>

          <div class="page-title-section">
            <h1 class="page-title">Projects Overview</h1>
            <p class="page-description">
              Manage and track all your active projects in one place
            </p>
          </div>

          <div class="page-actions">
            <button type="button" class="btn btn-secondary" data-action="export">
              <span class="icon-download" aria-hidden="true"></span>
              Export Data
            </button>
            <button type="button" class="btn btn-primary" data-action="create-project">
              <span class="icon-plus" aria-hidden="true"></span>
              New Project
            </button>
          </div>
        </div>

        <!-- Filters and search -->
        <section class="filters-section" aria-label="Project filters">
          <div class="filters-header">
            <h2 class="filters-title">Filter Projects</h2>
            <button type="button" class="btn btn-ghost btn-sm" data-action="clear-filters">
              Clear All
            </button>
          </div>

          <div class="filters-grid">
            <div class="filter-group">
              <label for="search-projects" class="filter-label">Search</label>
              <div class="search-input-wrapper">
                <input
                  type="search"
                  id="search-projects"
                  class="search-input"
                  placeholder="Search by name, description, or tags..."
                  aria-describedby="search-help"
                  autocomplete="off"
                  spellcheck="false"
                >
                <button type="button" class="search-clear" aria-label="Clear search" hidden>
                  <span class="icon-x" aria-hidden="true"></span>
                </button>
              </div>
              <div id="search-help" class="filter-help">
                Use keywords to find specific projects
              </div>
            </div>

            <div class="filter-group">
              <label for="status-filter" class="filter-label">Status</label>
              <select id="status-filter" class="filter-select" aria-describedby="status-help">
                <option value="">All Statuses</option>
                <option value="planning">Planning</option>
                <option value="active">Active</option>
                <option value="on-hold">On Hold</option>
                <option value="completed">Completed</option>
                <option value="cancelled">Cancelled</option>
              </select>
              <div id="status-help" class="filter-help">
                Filter by project status
              </div>
            </div>

            <div class="filter-group">
              <label for="team-filter" class="filter-label">Team</label>
              <select id="team-filter" class="filter-select" multiple aria-describedby="team-help">
                <option value="frontend">Frontend Team</option>
                <option value="backend">Backend Team</option>
                <option value="design">Design Team</option>
                <option value="qa">QA Team</option>
                <option value="devops">DevOps Team</option>
              </select>
              <div id="team-help" class="filter-help">
                Select one or more teams
              </div>
            </div>

            <div class="filter-group">
              <fieldset class="priority-fieldset">
                <legend class="filter-label">Priority</legend>
                <div class="checkbox-group">
                  <label class="checkbox-label">
                    <input type="checkbox" class="checkbox-input" name="priority" value="low">
                    <span class="checkbox-text">Low</span>
                  </label>
                  <label class="checkbox-label">
                    <input type="checkbox" class="checkbox-input" name="priority" value="medium">
                    <span class="checkbox-text">Medium</span>
                  </label>
                  <label class="checkbox-label">
                    <input type="checkbox" class="checkbox-input" name="priority" value="high">
                    <span class="checkbox-text">High</span>
                  </label>
                  <label class="checkbox-label">
                    <input type="checkbox" class="checkbox-input" name="priority" value="critical">
                    <span class="checkbox-text">Critical</span>
                  </label>
                </div>
              </fieldset>
            </div>
          </div>
        </section>
      </div>
    </main>

    <!-- Sidebar with additional information -->
    <aside class="sidebar" role="complementary" aria-label="Additional information">
      <div class="sidebar-content">
        <section class="sidebar-section">
          <h3 class="sidebar-title">Quick Stats</h3>
          <div class="stats-grid">
            <div class="stat-item">
              <div class="stat-value" data-value="24">24</div>
              <div class="stat-label">Active Projects</div>
            </div>
            <div class="stat-item">
              <div class="stat-value" data-value="156">156</div>
              <div class="stat-label">Total Tasks</div>
            </div>
            <div class="stat-item">
              <div class="stat-value" data-value="8">8</div>
              <div class="stat-label">Team Members</div>
            </div>
          </div>
        </section>

        <section class="sidebar-section">
          <h3 class="sidebar-title">Recent Activity</h3>
          <div class="activity-list">
            <article class="activity-item">
              <time class="activity-time" datetime="2024-01-15T14:30:00Z">
                2 hours ago
              </time>
              <div class="activity-content">
                <strong>Sarah Chen</strong> completed task "Design mockups"
              </div>
            </article>

            <article class="activity-item">
              <time class="activity-time" datetime="2024-01-15T12:15:00Z">
                4 hours ago
              </time>
              <div class="activity-content">
                <strong>Mike Johnson</strong> created new project "Mobile App Redesign"
              </div>
            </article>
          </div>
        </section>
      </div>
    </aside>

    <!-- Footer -->
    <footer class="footer" role="contentinfo">
      <div class="container">
        <div class="footer-content">
          <div class="footer-section">
            <h4 class="footer-title">Product</h4>
            <ul class="footer-links">
              <li><a href="/features">Features</a></li>
              <li><a href="/pricing">Pricing</a></li>
              <li><a href="/integrations">Integrations</a></li>
            </ul>
          </div>

          <div class="footer-section">
            <h4 class="footer-title">Support</h4>
            <ul class="footer-links">
              <li><a href="/help">Help Center</a></li>
              <li><a href="/contact">Contact Us</a></li>
              <li><a href="/status">System Status</a></li>
            </ul>
          </div>

          <div class="footer-section">
            <h4 class="footer-title">Legal</h4>
            <ul class="footer-links">
              <li><a href="/privacy">Privacy Policy</a></li>
              <li><a href="/terms">Terms of Service</a></li>
              <li><a href="/cookies">Cookie Policy</a></li>
            </ul>
          </div>
        </div>

        <div class="footer-bottom">
          <p class="copyright">
            &copy; 2024 Project Manager Pro. All rights reserved.
          </p>

          <div class="social-links">
            <a href="https://twitter.com/projectmanagerpro" class="social-link" aria-label="Follow us on Twitter">
              <span class="icon-twitter" aria-hidden="true"></span>
            </a>
            <a href="https://github.com/projectmanagerpro" class="social-link" aria-label="View our code on GitHub">
              <span class="icon-github" aria-hidden="true"></span>
            </a>
            <a href="https://linkedin.com/company/projectmanagerpro" class="social-link" aria-label="Connect with us on LinkedIn">
              <span class="icon-linkedin" aria-hidden="true"></span>
            </a>
          </div>
        </div>
      </div>
    </footer>
  </div>

  <!-- Scripts -->
  <script src="/js/vendor/polyfills.js" defer></script>
  <script src="/js/main.js" type="module" defer></script>

  <!-- Service Worker Registration -->
  <script>
    if ('serviceWorker' in navigator) {
      window.addEventListener('load', () => {
        navigator.serviceWorker.register('/sw.js')
          .then(registration => {
            console.log('SW registered: ', registration);
          })
          .catch(registrationError => {
            console.log('SW registration failed: ', registrationError);
          });
      });
    }
  </script>
</body>
</html>
`;

      const result = await parserManager.parseFile('index.html', htmlCode);
      const extractor = new HTMLExtractor('html', 'index.html', htmlCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Document structure
      const htmlElement = symbols.find(s => s.name === 'html');
      expect(htmlElement).toBeDefined();
      expect(htmlElement?.kind).toBe(SymbolKind.Class); // HTML elements as classes
      expect(htmlElement?.signature).toContain('lang="en"');
      expect(htmlElement?.signature).toContain('data-theme="light"');

      const headElement = symbols.find(s => s.name === 'head');
      expect(headElement).toBeDefined();

      const bodyElement = symbols.find(s => s.name === 'body');
      expect(bodyElement).toBeDefined();
      expect(bodyElement?.signature).toContain('class="theme-light"');

      // Meta elements
      const charsetMeta = symbols.find(s => s.signature?.includes('charset="UTF-8"'));
      expect(charsetMeta).toBeDefined();
      expect(charsetMeta?.kind).toBe(SymbolKind.Property);

      const viewportMeta = symbols.find(s => s.signature?.includes('name="viewport"'));
      expect(viewportMeta).toBeDefined();

      const descriptionMeta = symbols.find(s => s.signature?.includes('name="description"'));
      expect(descriptionMeta).toBeDefined();

      const ogTitleMeta = symbols.find(s => s.signature?.includes('property="og:title"'));
      expect(ogTitleMeta).toBeDefined();

      const twitterMeta = symbols.find(s => s.signature?.includes('name="twitter:card"'));
      expect(twitterMeta).toBeDefined();

      // Title
      const titleElement = symbols.find(s => s.name === 'title');
      expect(titleElement).toBeDefined();
      expect(titleElement?.signature).toContain('Project Manager Pro');

      // Link elements
      const stylesheetLink = symbols.find(s => s.signature?.includes('rel="stylesheet"') && s.signature?.includes('main.css'));
      expect(stylesheetLink).toBeDefined();
      expect(stylesheetLink?.kind).toBe(SymbolKind.Import);

      const preconnectLink = symbols.find(s => s.signature?.includes('rel="preconnect"'));
      expect(preconnectLink).toBeDefined();

      const preloadLink = symbols.find(s => s.signature?.includes('rel="preload"'));
      expect(preloadLink).toBeDefined();

      const iconLink = symbols.find(s => s.signature?.includes('rel="icon"'));
      expect(iconLink).toBeDefined();

      const manifestLink = symbols.find(s => s.signature?.includes('rel="manifest"'));
      expect(manifestLink).toBeDefined();

      // Structured data script
      const structuredDataScript = symbols.find(s => s.signature?.includes('application/ld+json'));
      expect(structuredDataScript).toBeDefined();
      expect(structuredDataScript?.kind).toBe(SymbolKind.Variable);

      // Semantic elements
      const headerElement = symbols.find(s => s.name === 'header');
      expect(headerElement).toBeDefined();
      expect(headerElement?.signature).toContain('role="banner"');

      const navElement = symbols.find(s => s.name === 'nav');
      expect(navElement).toBeDefined();
      expect(navElement?.signature).toContain('role="navigation"');

      const mainElement = symbols.find(s => s.name === 'main');
      expect(mainElement).toBeDefined();
      expect(mainElement?.signature).toContain('id="main-content"');
      expect(mainElement?.signature).toContain('role="main"');

      const asideElement = symbols.find(s => s.name === 'aside');
      expect(asideElement).toBeDefined();
      expect(asideElement?.signature).toContain('role="complementary"');

      const footerElement = symbols.find(s => s.name === 'footer');
      expect(footerElement).toBeDefined();
      expect(footerElement?.signature).toContain('role="contentinfo"');

      // Accessibility elements
      const skipLink = symbols.find(s => s.signature?.includes('Skip to main content'));
      expect(skipLink).toBeDefined();

      const logoImg = symbols.find(s => s.signature?.includes('alt="Project Manager Pro"'));
      expect(logoImg).toBeDefined();

      // Interactive elements
      const themeButton = symbols.find(s => s.signature?.includes('data-action="toggle-theme"'));
      expect(themeButton).toBeDefined();

      const notificationButton = symbols.find(s => s.signature?.includes('data-badge="3"'));
      expect(notificationButton).toBeDefined();

      const userMenuButton = symbols.find(s => s.signature?.includes('aria-expanded="false"') && s.signature?.includes('aria-haspopup="true"'));
      expect(userMenuButton).toBeDefined();

      // Form elements
      const searchInput = symbols.find(s => s.signature?.includes('type="search"') && s.signature?.includes('id="search-projects"'));
      expect(searchInput).toBeDefined();
      expect(searchInput?.kind).toBe(SymbolKind.Field);

      const statusSelect = symbols.find(s => s.signature?.includes('id="status-filter"'));
      expect(statusSelect).toBeDefined();

      const teamSelect = symbols.find(s => s.signature?.includes('id="team-filter"') && s.signature?.includes('multiple'));
      expect(teamSelect).toBeDefined();

      const fieldsetElement = symbols.find(s => s.name === 'fieldset');
      expect(fieldsetElement).toBeDefined();

      const legendElement = symbols.find(s => s.name === 'legend');
      expect(legendElement).toBeDefined();

      // Checkbox inputs
      const checkboxInputs = symbols.filter(s => s.signature?.includes('type="checkbox"'));
      expect(checkboxInputs.length).toBeGreaterThanOrEqual(4);

      // Data attributes
      const componentDropdown = symbols.find(s => s.signature?.includes('data-component="dropdown"'));
      expect(componentDropdown).toBeDefined();

      const envAttribute = symbols.find(s => s.signature?.includes('data-env="production"'));
      expect(envAttribute).toBeDefined();

      // ARIA attributes
      const ariaCurrentPage = symbols.find(s => s.signature?.includes('aria-current="page"'));
      expect(ariaCurrentPage).toBeDefined();

      const ariaHidden = symbols.filter(s => s.signature?.includes('aria-hidden="true"'));
      expect(ariaHidden.length).toBeGreaterThan(5);

      const ariaLabel = symbols.filter(s => s.signature?.includes('aria-label='));
      expect(ariaLabel.length).toBeGreaterThan(8);

      // Breadcrumbs
      const breadcrumbNav = symbols.find(s => s.signature?.includes('aria-label="Breadcrumb"'));
      expect(breadcrumbNav).toBeDefined();

      const breadcrumbList = symbols.find(s => s.name === 'ol' && s.signature?.includes('breadcrumb'));
      expect(breadcrumbList).toBeDefined();

      // Time elements
      const timeElements = symbols.filter(s => s.name === 'time');
      expect(timeElements.length).toBeGreaterThanOrEqual(2);

      const datetimeAttribute = symbols.find(s => s.signature?.includes('datetime="2024-01-15T14:30:00Z"'));
      expect(datetimeAttribute).toBeDefined();

      // Article elements
      const articleElements = symbols.filter(s => s.name === 'article');
      expect(articleElements.length).toBeGreaterThanOrEqual(2);

      // Script elements
      const polyfillsScript = symbols.find(s => s.signature?.includes('src="/js/vendor/polyfills.js"'));
      expect(polyfillsScript).toBeDefined();
      expect(polyfillsScript?.kind).toBe(SymbolKind.Import);

      const moduleScript = symbols.find(s => s.signature?.includes('type="module"'));
      expect(moduleScript).toBeDefined();

      const serviceWorkerScript = symbols.find(s => s.signature?.includes('serviceWorker'));
      expect(serviceWorkerScript).toBeDefined();
    });
  });

  describe('Form Elements and Interactive Components', () => {
    it('should extract complex forms, validation, and interactive elements', async () => {
      const htmlCode = `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>Advanced Form Example</title>
</head>
<body>
  <!-- Contact Form with Validation -->
  <section class="form-section">
    <h2>Contact Information</h2>

    <form id="contact-form" class="contact-form" novalidate aria-label="Contact form">
      <div class="form-row">
        <div class="form-group">
          <label for="first-name" class="form-label required">
            First Name
            <span class="required-indicator" aria-label="required">*</span>
          </label>
          <input
            type="text"
            id="first-name"
            name="firstName"
            class="form-input"
            required
            autocomplete="given-name"
            aria-describedby="first-name-error"
            minlength="2"
            maxlength="50"
            pattern="[A-Za-z\\s]+"
            placeholder="Enter your first name"
          >
          <div id="first-name-error" class="error-message" role="alert" aria-live="polite"></div>
        </div>

        <div class="form-group">
          <label for="last-name" class="form-label required">
            Last Name
            <span class="required-indicator" aria-label="required">*</span>
          </label>
          <input
            type="text"
            id="last-name"
            name="lastName"
            class="form-input"
            required
            autocomplete="family-name"
            aria-describedby="last-name-error"
            minlength="2"
            maxlength="50"
            pattern="[A-Za-z\\s]+"
            placeholder="Enter your last name"
          >
          <div id="last-name-error" class="error-message" role="alert" aria-live="polite"></div>
        </div>
      </div>

      <div class="form-group">
        <label for="email" class="form-label required">
          Email Address
          <span class="required-indicator" aria-label="required">*</span>
        </label>
        <input
          type="email"
          id="email"
          name="email"
          class="form-input"
          required
          autocomplete="email"
          aria-describedby="email-help email-error"
          placeholder="your.email@example.com"
        >
        <div id="email-help" class="form-help">
          We'll never share your email with anyone else.
        </div>
        <div id="email-error" class="error-message" role="alert" aria-live="polite"></div>
      </div>

      <div class="form-group">
        <label for="phone" class="form-label">
          Phone Number
          <span class="optional-indicator">(optional)</span>
        </label>
        <input
          type="tel"
          id="phone"
          name="phone"
          class="form-input"
          autocomplete="tel"
          aria-describedby="phone-help"
          pattern="[0-9\\s\\-\\+\\(\\)]+"
          placeholder="(555) 123-4567"
        >
        <div id="phone-help" class="form-help">
          Include country code for international numbers.
        </div>
      </div>

      <div class="form-group">
        <label for="company" class="form-label">Company</label>
        <input
          type="text"
          id="company"
          name="company"
          class="form-input"
          autocomplete="organization"
          list="company-suggestions"
          placeholder="Your company name"
        >
        <datalist id="company-suggestions">
          <option value="Tech Corp">
          <option value="Innovation Labs">
          <option value="Digital Solutions">
          <option value="Creative Agency">
          <option value="Startup Inc">
        </datalist>
      </div>

      <div class="form-group">
        <label for="country" class="form-label required">
          Country
          <span class="required-indicator" aria-label="required">*</span>
        </label>
        <select id="country" name="country" class="form-select" required aria-describedby="country-error">
          <option value="">Select your country</option>
          <option value="us">United States</option>
          <option value="ca">Canada</option>
          <option value="uk">United Kingdom</option>
          <option value="de">Germany</option>
          <option value="fr">France</option>
          <option value="jp">Japan</option>
          <option value="au">Australia</option>
          <option value="other">Other</option>
        </select>
        <div id="country-error" class="error-message" role="alert" aria-live="polite"></div>
      </div>

      <div class="form-group">
        <fieldset class="form-fieldset">
          <legend class="form-legend required">
            Preferred Contact Method
            <span class="required-indicator" aria-label="required">*</span>
          </legend>
          <div class="radio-group" role="radiogroup" aria-describedby="contact-method-error">
            <label class="radio-label">
              <input type="radio" name="contactMethod" value="email" class="radio-input" required>
              <span class="radio-text">Email</span>
            </label>
            <label class="radio-label">
              <input type="radio" name="contactMethod" value="phone" class="radio-input" required>
              <span class="radio-text">Phone</span>
            </label>
            <label class="radio-label">
              <input type="radio" name="contactMethod" value="both" class="radio-input" required>
              <span class="radio-text">Both Email and Phone</span>
            </label>
          </div>
          <div id="contact-method-error" class="error-message" role="alert" aria-live="polite"></div>
        </fieldset>
      </div>

      <div class="form-group">
        <fieldset class="form-fieldset">
          <legend class="form-legend">Interests</legend>
          <div class="checkbox-group" aria-describedby="interests-help">
            <label class="checkbox-label">
              <input type="checkbox" name="interests" value="web-development" class="checkbox-input">
              <span class="checkbox-text">Web Development</span>
            </label>
            <label class="checkbox-label">
              <input type="checkbox" name="interests" value="mobile-apps" class="checkbox-input">
              <span class="checkbox-text">Mobile Apps</span>
            </label>
            <label class="checkbox-label">
              <input type="checkbox" name="interests" value="ui-design" class="checkbox-input">
              <span class="checkbox-text">UI/UX Design</span>
            </label>
            <label class="checkbox-label">
              <input type="checkbox" name="interests" value="data-science" class="checkbox-input">
              <span class="checkbox-text">Data Science</span>
            </label>
          </div>
          <div id="interests-help" class="form-help">
            Select all that apply to your interests.
          </div>
        </fieldset>
      </div>

      <div class="form-group">
        <label for="experience" class="form-label">Years of Experience</label>
        <input
          type="range"
          id="experience"
          name="experience"
          class="range-input"
          min="0"
          max="20"
          step="1"
          value="5"
          aria-describedby="experience-value experience-help"
        >
        <div class="range-display">
          <span id="experience-value" class="range-value">5 years</span>
        </div>
        <div id="experience-help" class="form-help">
          Drag the slider to indicate your years of experience.
        </div>
      </div>

      <div class="form-group">
        <label for="budget" class="form-label">Project Budget</label>
        <input
          type="number"
          id="budget"
          name="budget"
          class="form-input"
          min="1000"
          max="1000000"
          step="500"
          placeholder="50000"
          aria-describedby="budget-help"
        >
        <div id="budget-help" class="form-help">
          Enter your budget in USD. Minimum $1,000.
        </div>
      </div>

      <div class="form-group">
        <label for="start-date" class="form-label">Preferred Start Date</label>
        <input
          type="date"
          id="start-date"
          name="startDate"
          class="form-input"
          min="2024-01-01"
          max="2025-12-31"
          aria-describedby="start-date-help"
        >
        <div id="start-date-help" class="form-help">
          When would you like to start the project?
        </div>
      </div>

      <div class="form-group">
        <label for="message" class="form-label required">
          Project Description
          <span class="required-indicator" aria-label="required">*</span>
        </label>
        <textarea
          id="message"
          name="message"
          class="form-textarea"
          required
          rows="6"
          maxlength="1000"
          placeholder="Please describe your project requirements, goals, and any specific needs..."
          aria-describedby="message-counter message-error"
        ></textarea>
        <div class="textarea-footer">
          <div id="message-counter" class="character-counter">
            <span class="current-count">0</span> / <span class="max-count">1000</span> characters
          </div>
        </div>
        <div id="message-error" class="error-message" role="alert" aria-live="polite"></div>
      </div>

      <div class="form-group">
        <label for="file-upload" class="form-label">
          Attachments
          <span class="optional-indicator">(optional)</span>
        </label>
        <div class="file-upload-wrapper">
          <input
            type="file"
            id="file-upload"
            name="attachments"
            class="file-input"
            multiple
            accept=".pdf,.doc,.docx,.txt,.jpg,.jpeg,.png,.gif"
            aria-describedby="file-upload-help file-upload-list"
          >
          <label for="file-upload" class="file-upload-label">
            <span class="file-upload-icon" aria-hidden="true">📎</span>
            <span class="file-upload-text">Choose files or drag and drop</span>
          </label>
        </div>
        <div id="file-upload-help" class="form-help">
          Accepted formats: PDF, DOC, DOCX, TXT, JPG, PNG, GIF. Max 5MB per file.
        </div>
        <div id="file-upload-list" class="file-list" aria-live="polite"></div>
      </div>

      <div class="form-group">
        <div class="checkbox-group">
          <label class="checkbox-label">
            <input
              type="checkbox"
              name="newsletter"
              value="yes"
              class="checkbox-input"
              aria-describedby="newsletter-help"
            >
            <span class="checkbox-text">
              Subscribe to our newsletter for updates and tips
            </span>
          </label>
        </div>
        <div id="newsletter-help" class="form-help">
          You can unsubscribe at any time.
        </div>
      </div>

      <div class="form-group">
        <div class="checkbox-group">
          <label class="checkbox-label required">
            <input
              type="checkbox"
              name="terms"
              value="accepted"
              class="checkbox-input"
              required
              aria-describedby="terms-error"
            >
            <span class="checkbox-text">
              I agree to the <a href="/terms" target="_blank" rel="noopener">Terms of Service</a>
              and <a href="/privacy" target="_blank" rel="noopener">Privacy Policy</a>
              <span class="required-indicator" aria-label="required">*</span>
            </span>
          </label>
        </div>
        <div id="terms-error" class="error-message" role="alert" aria-live="polite"></div>
      </div>

      <div class="form-actions">
        <button type="button" class="btn btn-secondary" data-action="save-draft">
          Save as Draft
        </button>
        <button type="reset" class="btn btn-ghost">
          Clear Form
        </button>
        <button type="submit" class="btn btn-primary">
          <span class="btn-text">Send Message</span>
          <span class="btn-loading" aria-hidden="true">Sending...</span>
        </button>
      </div>
    </form>
  </section>

  <!-- Modal Dialog -->
  <dialog id="confirmation-modal" class="modal" aria-labelledby="modal-title" aria-describedby="modal-description">
    <div class="modal-content">
      <header class="modal-header">
        <h3 id="modal-title" class="modal-title">Confirm Submission</h3>
        <button type="button" class="modal-close" aria-label="Close dialog" data-action="close-modal">
          <span aria-hidden="true">&times;</span>
        </button>
      </header>

      <div class="modal-body">
        <p id="modal-description">
          Are you sure you want to submit this form? Please review your information before proceeding.
        </p>
      </div>

      <footer class="modal-footer">
        <button type="button" class="btn btn-secondary" data-action="cancel">
          Cancel
        </button>
        <button type="button" class="btn btn-primary" data-action="confirm-submit">
          Confirm & Submit
        </button>
      </footer>
    </div>
  </dialog>

  <!-- Details/Summary Disclosure -->
  <details class="disclosure" open>
    <summary class="disclosure-summary">
      <span class="summary-text">Advanced Options</span>
      <span class="summary-icon" aria-hidden="true">▼</span>
    </summary>

    <div class="disclosure-content">
      <div class="form-group">
        <label for="timezone" class="form-label">Timezone</label>
        <select id="timezone" name="timezone" class="form-select">
          <option value="">Auto-detect</option>
          <option value="UTC">UTC</option>
          <option value="EST">Eastern Standard Time</option>
          <option value="PST">Pacific Standard Time</option>
          <option value="GMT">Greenwich Mean Time</option>
        </select>
      </div>

      <div class="form-group">
        <label for="referral" class="form-label">How did you hear about us?</label>
        <select id="referral" name="referral" class="form-select">
          <option value="">Please select</option>
          <option value="search">Search Engine</option>
          <option value="social">Social Media</option>
          <option value="referral">Friend/Colleague</option>
          <option value="advertisement">Advertisement</option>
          <option value="other">Other</option>
        </select>
      </div>
    </div>
  </details>
</body>
</html>
`;

      const result = await parserManager.parseFile('form.html', htmlCode);
      const extractor = new HTMLExtractor('html', 'form.html', htmlCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Form element
      const contactForm = symbols.find(s => s.signature?.includes('id="contact-form"'));
      expect(contactForm).toBeDefined();
      expect(contactForm?.signature).toContain('novalidate');
      expect(contactForm?.signature).toContain('aria-label="Contact form"');

      // Input elements with validation
      const firstNameInput = symbols.find(s => s.signature?.includes('id="first-name"'));
      expect(firstNameInput).toBeDefined();
      expect(firstNameInput?.kind).toBe(SymbolKind.Field);
      expect(firstNameInput?.signature).toContain('required');
      expect(firstNameInput?.signature).toContain('autocomplete="given-name"');
      expect(firstNameInput?.signature).toContain('pattern="[A-Za-z\\s]+"');

      const emailInput = symbols.find(s => s.signature?.includes('type="email"'));
      expect(emailInput).toBeDefined();
      expect(emailInput?.signature).toContain('autocomplete="email"');

      const phoneInput = symbols.find(s => s.signature?.includes('type="tel"'));
      expect(phoneInput).toBeDefined();
      expect(phoneInput?.signature).toContain('pattern="[0-9\\s\\-\\+\\(\\)]+"');

      // Datalist
      const datalistElement = symbols.find(s => s.name === 'datalist');
      expect(datalistElement).toBeDefined();
      expect(datalistElement?.signature).toContain('id="company-suggestions"');

      const datalistOptions = symbols.filter(s => s.signature?.includes('value="Tech Corp"'));
      expect(datalistOptions.length).toBeGreaterThan(0);

      // Select element
      const countrySelect = symbols.find(s => s.signature?.includes('id="country"'));
      expect(countrySelect).toBeDefined();
      expect(countrySelect?.signature).toContain('required');

      const selectOptions = symbols.filter(s => s.signature?.includes('value="us"'));
      expect(selectOptions.length).toBeGreaterThan(0);

      // Radio buttons
      const radioInputs = symbols.filter(s => s.signature?.includes('type="radio"'));
      expect(radioInputs.length).toBe(3);

      const emailRadio = symbols.find(s => s.signature?.includes('name="contactMethod"') && s.signature?.includes('value="email"'));
      expect(emailRadio).toBeDefined();

      // Checkboxes
      const checkboxInputs = symbols.filter(s => s.signature?.includes('type="checkbox"'));
      expect(checkboxInputs.length).toBeGreaterThanOrEqual(6);

      const webDevCheckbox = symbols.find(s => s.signature?.includes('value="web-development"'));
      expect(webDevCheckbox).toBeDefined();

      // Range input
      const rangeInput = symbols.find(s => s.signature?.includes('type="range"'));
      expect(rangeInput).toBeDefined();
      expect(rangeInput?.signature).toContain('min="0"');
      expect(rangeInput?.signature).toContain('max="20"');
      expect(rangeInput?.signature).toContain('step="1"');

      // Number input
      const numberInput = symbols.find(s => s.signature?.includes('type="number"'));
      expect(numberInput).toBeDefined();
      expect(numberInput?.signature).toContain('min="1000"');
      expect(numberInput?.signature).toContain('max="1000000"');

      // Date input
      const dateInput = symbols.find(s => s.signature?.includes('type="date"'));
      expect(dateInput).toBeDefined();
      expect(dateInput?.signature).toContain('min="2024-01-01"');

      // Textarea
      const textareaElement = symbols.find(s => s.name === 'textarea');
      expect(textareaElement).toBeDefined();
      expect(textareaElement?.signature).toContain('maxlength="1000"');
      expect(textareaElement?.signature).toContain('rows="6"');

      // File input
      const fileInput = symbols.find(s => s.signature?.includes('type="file"'));
      expect(fileInput).toBeDefined();
      expect(fileInput?.signature).toContain('multiple');
      expect(fileInput?.signature).toContain('accept=".pdf,.doc,.docx,.txt,.jpg,.jpeg,.png,.gif"');

      // Fieldset and legend
      const fieldsetElements = symbols.filter(s => s.name === 'fieldset');
      expect(fieldsetElements.length).toBeGreaterThanOrEqual(2);

      const legendElements = symbols.filter(s => s.name === 'legend');
      expect(legendElements.length).toBeGreaterThanOrEqual(2);

      // Form buttons
      const submitButton = symbols.find(s => s.signature?.includes('type="submit"'));
      expect(submitButton).toBeDefined();

      const resetButton = symbols.find(s => s.signature?.includes('type="reset"'));
      expect(resetButton).toBeDefined();

      const saveDraftButton = symbols.find(s => s.signature?.includes('data-action="save-draft"'));
      expect(saveDraftButton).toBeDefined();

      // ARIA attributes
      const ariaDescribedBy = symbols.filter(s => s.signature?.includes('aria-describedby='));
      expect(ariaDescribedBy.length).toBeGreaterThan(10);

      const ariaLive = symbols.filter(s => s.signature?.includes('aria-live="polite"'));
      expect(ariaLive.length).toBeGreaterThan(5);

      const roleAlert = symbols.filter(s => s.signature?.includes('role="alert"'));
      expect(roleAlert.length).toBeGreaterThan(5);

      const roleRadiogroup = symbols.find(s => s.signature?.includes('role="radiogroup"'));
      expect(roleRadiogroup).toBeDefined();

      // Modal dialog
      const dialogElement = symbols.find(s => s.name === 'dialog');
      expect(dialogElement).toBeDefined();
      expect(dialogElement?.signature).toContain('aria-labelledby="modal-title"');
      expect(dialogElement?.signature).toContain('aria-describedby="modal-description"');

      // Details/Summary
      const detailsElement = symbols.find(s => s.name === 'details');
      expect(detailsElement).toBeDefined();
      expect(detailsElement?.signature).toContain('open');

      const summaryElement = symbols.find(s => s.name === 'summary');
      expect(summaryElement).toBeDefined();

      // Required indicators
      const requiredIndicators = symbols.filter(s => s.signature?.includes('required-indicator'));
      expect(requiredIndicators.length).toBeGreaterThan(5);

      // Error message containers
      const errorMessages = symbols.filter(s => s.signature?.includes('error-message'));
      expect(errorMessages.length).toBeGreaterThanOrEqual(7);

      // Help text elements
      const helpTexts = symbols.filter(s => s.signature?.includes('form-help'));
      expect(helpTexts.length).toBeGreaterThanOrEqual(8);

      // Links with proper attributes
      const externalLinks = symbols.filter(s => s.signature?.includes('target="_blank"') && s.signature?.includes('rel="noopener"'));
      expect(externalLinks.length).toBeGreaterThanOrEqual(2);
    });
  });

  describe('Media Elements and Embedded Content', () => {
    it('should extract multimedia elements, SVG, canvas, and embedded content', async () => {
      const htmlCode = `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>Media and Embedded Content</title>
</head>
<body>
  <!-- Image Gallery -->
  <section class="gallery" aria-label="Photo gallery">
    <h2>Image Gallery</h2>

    <figure class="featured-image">
      <img
        src="/images/hero-image.jpg"
        alt="Beautiful sunset over mountains with vibrant orange and pink colors"
        width="800"
        height="600"
        loading="lazy"
        decoding="async"
        sizes="(max-width: 768px) 100vw, (max-width: 1200px) 50vw, 33vw"
        srcset="
          /images/hero-image-400.jpg 400w,
          /images/hero-image-800.jpg 800w,
          /images/hero-image-1200.jpg 1200w,
          /images/hero-image-1600.jpg 1600w
        "
      >
      <figcaption class="image-caption">
        Sunset over the Rocky Mountains -
        <cite>Photo by Jane Photographer</cite>
        <time datetime="2024-01-15">January 15, 2024</time>
      </figcaption>
    </figure>

    <div class="image-grid">
      <picture class="responsive-image">
        <source
          media="(min-width: 1200px)"
          srcset="/images/gallery-1-large.webp"
          type="image/webp"
        >
        <source
          media="(min-width: 768px)"
          srcset="/images/gallery-1-medium.webp"
          type="image/webp"
        >
        <source
          srcset="/images/gallery-1-small.webp"
          type="image/webp"
        >
        <img
          src="/images/gallery-1-medium.jpg"
          alt="Abstract art piece with geometric patterns"
          loading="lazy"
          decoding="async"
        >
      </picture>

      <img
        src="/images/gallery-2.jpg"
        alt="Modern architecture with glass and steel elements"
        width="400"
        height="300"
        loading="lazy"
        decoding="async"
      >

      <img
        src="data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iMjAwIiBoZWlnaHQ9IjIwMCIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj4KICA8cmVjdCB3aWR0aD0iMTAwJSIgaGVpZ2h0PSIxMDAlIiBmaWxsPSIjZGRkIi8+CiAgPHRleHQgeD0iNTAlIiB5PSI1MCUiIGZvbnQtZmFtaWx5PSJBcmlhbCwgc2Fucy1zZXJpZiIgZm9udC1zaXplPSIxNnB4IiBmaWxsPSIjOTk5IiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBkeT0iLjNlbSI+UGxhY2Vob2xkZXI8L3RleHQ+Cjwvc3ZnPgo="
        alt="Placeholder image"
        width="200"
        height="200"
        loading="lazy"
      >
    </div>
  </section>

  <!-- Video Section -->
  <section class="video-section" aria-label="Video content">
    <h2>Video Content</h2>

    <div class="video-wrapper">
      <video
        id="main-video"
        class="main-video"
        width="800"
        height="450"
        controls
        preload="metadata"
        poster="/images/video-poster.jpg"
        aria-describedby="video-description"
      >
        <source src="/videos/demo.mp4" type="video/mp4">
        <source src="/videos/demo.webm" type="video/webm">
        <source src="/videos/demo.ogv" type="video/ogg">

        <track
          kind="subtitles"
          src="/videos/demo-en.vtt"
          srclang="en"
          label="English"
          default
        >
        <track
          kind="subtitles"
          src="/videos/demo-es.vtt"
          srclang="es"
          label="Español"
        >
        <track
          kind="captions"
          src="/videos/demo-captions.vtt"
          srclang="en"
          label="English Captions"
        >
        <track
          kind="descriptions"
          src="/videos/demo-descriptions.vtt"
          srclang="en"
          label="Audio Descriptions"
        >

        <p class="video-fallback">
          Your browser doesn't support HTML5 video.
          <a href="/videos/demo.mp4">Download the video</a> instead.
        </p>
      </video>

      <div id="video-description" class="video-description">
        Product demonstration showing key features and user interface walkthrough.
      </div>
    </div>

    <!-- Custom Video Controls -->
    <div class="video-controls" aria-label="Video controls">
      <button type="button" class="control-btn" data-action="play-pause" aria-label="Play/Pause">
        <span class="icon-play" aria-hidden="true"></span>
        <span class="icon-pause" aria-hidden="true" hidden></span>
      </button>

      <div class="progress-container">
        <input
          type="range"
          class="progress-bar"
          min="0"
          max="100"
          value="0"
          step="0.1"
          aria-label="Video progress"
        >
        <div class="time-display">
          <span class="current-time">0:00</span> / <span class="total-time">0:00</span>
        </div>
      </div>

      <button type="button" class="control-btn" data-action="mute" aria-label="Mute/Unmute">
        <span class="icon-volume" aria-hidden="true"></span>
        <span class="icon-mute" aria-hidden="true" hidden></span>
      </button>

      <input
        type="range"
        class="volume-control"
        min="0"
        max="1"
        step="0.1"
        value="1"
        aria-label="Volume"
      >

      <button type="button" class="control-btn" data-action="fullscreen" aria-label="Toggle fullscreen">
        <span class="icon-fullscreen" aria-hidden="true"></span>
      </button>
    </div>
  </section>

  <!-- Audio Section -->
  <section class="audio-section" aria-label="Audio content">
    <h2>Audio Content</h2>

    <div class="audio-player">
      <audio
        id="podcast-player"
        class="audio-element"
        preload="none"
        aria-describedby="audio-description"
      >
        <source src="/audio/podcast-episode-1.mp3" type="audio/mpeg">
        <source src="/audio/podcast-episode-1.ogg" type="audio/ogg">
        <source src="/audio/podcast-episode-1.wav" type="audio/wav">

        <p class="audio-fallback">
          Your browser doesn't support HTML5 audio.
          <a href="/audio/podcast-episode-1.mp3">Download the audio file</a> instead.
        </p>
      </audio>

      <div class="audio-info">
        <h3 class="audio-title">Tech Talk Episode 1: Web Accessibility</h3>
        <p id="audio-description" class="audio-description">
          In this episode, we discuss the importance of web accessibility and practical tips for developers.
        </p>
        <div class="audio-meta">
          <span class="duration">Duration: 45 minutes</span>
          <span class="file-size">Size: 32.5 MB</span>
        </div>
      </div>
    </div>
  </section>

  <!-- SVG Graphics -->
  <section class="graphics-section" aria-label="Vector graphics">
    <h2>SVG Graphics</h2>

    <div class="svg-container">
      <!-- Inline SVG -->
      <svg
        width="300"
        height="200"
        viewBox="0 0 300 200"
        xmlns="http://www.w3.org/2000/svg"
        role="img"
        aria-labelledby="chart-title chart-desc"
      >
        <title id="chart-title">Sales Data Chart</title>
        <desc id="chart-desc">
          Bar chart showing quarterly sales data with values for Q1: 100, Q2: 150, Q3: 200, Q4: 175
        </desc>

        <defs>
          <linearGradient id="barGradient" x1="0%" y1="0%" x2="0%" y2="100%">
            <stop offset="0%" style="stop-color:#4285f4;stop-opacity:1" />
            <stop offset="100%" style="stop-color:#1a73e8;stop-opacity:1" />
          </linearGradient>

          <pattern id="stripes" patternUnits="userSpaceOnUse" width="4" height="4">
            <rect width="4" height="4" fill="#f0f0f0"/>
            <rect width="2" height="4" fill="#ddd"/>
          </pattern>
        </defs>

        <!-- Chart background -->
        <rect x="0" y="0" width="300" height="200" fill="#fafafa" stroke="#ddd" stroke-width="1"/>

        <!-- Data bars -->
        <rect x="40" y="120" width="40" height="60" fill="url(#barGradient)" aria-label="Q1: 100">
          <title>Q1: $100k</title>
        </rect>
        <rect x="100" y="95" width="40" height="85" fill="url(#barGradient)" aria-label="Q2: 150">
          <title>Q2: $150k</title>
        </rect>
        <rect x="160" y="70" width="40" height="110" fill="url(#barGradient)" aria-label="Q3: 200">
          <title>Q3: $200k</title>
        </rect>
        <rect x="220" y="82" width="40" height="98" fill="url(#barGradient)" aria-label="Q4: 175">
          <title>Q4: $175k</title>
        </rect>

        <!-- Labels -->
        <text x="60" y="195" text-anchor="middle" font-family="Arial, sans-serif" font-size="12" fill="#666">Q1</text>
        <text x="120" y="195" text-anchor="middle" font-family="Arial, sans-serif" font-size="12" fill="#666">Q2</text>
        <text x="180" y="195" text-anchor="middle" font-family="Arial, sans-serif" font-size="12" fill="#666">Q3</text>
        <text x="240" y="195" text-anchor="middle" font-family="Arial, sans-serif" font-size="12" fill="#666">Q4</text>

        <!-- Interactive elements -->
        <circle cx="150" cy="50" r="20" fill="#ff6b6b" opacity="0.8">
          <animate attributeName="r" values="15;25;15" dur="2s" repeatCount="indefinite"/>
        </circle>
      </svg>

      <!-- SVG from external file -->
      <img src="/images/logo.svg" alt="Company logo" width="150" height="75" class="svg-logo">

      <!-- SVG with object tag -->
      <object data="/images/infographic.svg" type="image/svg+xml" width="400" height="300" aria-label="Data infographic">
        <img src="/images/infographic-fallback.png" alt="Data infographic showing key statistics">
      </object>
    </div>
  </section>

  <!-- Canvas and Interactive Graphics -->
  <section class="canvas-section" aria-label="Interactive graphics">
    <h2>Canvas Graphics</h2>

    <div class="canvas-container">
      <canvas
        id="interactive-chart"
        class="chart-canvas"
        width="600"
        height="400"
        role="img"
        aria-label="Interactive data visualization"
        aria-describedby="canvas-description"
      >
        <p id="canvas-description">
          Interactive chart showing real-time data. Canvas is not supported in your browser.
          <a href="/data.csv">Download the raw data</a> instead.
        </p>
      </canvas>

      <div class="canvas-controls">
        <button type="button" class="btn" data-action="update-chart">Refresh Data</button>
        <button type="button" class="btn" data-action="export-chart">Export as PNG</button>
        <label>
          Chart Type:
          <select id="chart-type" data-action="change-chart-type">
            <option value="line">Line Chart</option>
            <option value="bar">Bar Chart</option>
            <option value="pie">Pie Chart</option>
            <option value="scatter">Scatter Plot</option>
          </select>
        </label>
      </div>
    </div>
  </section>

  <!-- Embedded Content -->
  <section class="embed-section" aria-label="Embedded content">
    <h2>Embedded Content</h2>

    <!-- YouTube embed -->
    <div class="video-embed">
      <iframe
        width="560"
        height="315"
        src="https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ"
        title="Sample Video"
        frameborder="0"
        allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
        allowfullscreen
        loading="lazy"
        aria-describedby="embed-description"
      ></iframe>
      <div id="embed-description" class="embed-description">
        Educational video about web development best practices.
      </div>
    </div>

    <!-- Map embed -->
    <div class="map-embed">
      <iframe
        src="https://www.openstreetmap.org/export/embed.html?bbox=-0.004017949104309083%2C51.47612752641776%2C0.00030577182769775396%2C51.478569861898606&layer=mapnik"
        width="400"
        height="300"
        frameborder="0"
        title="Office location map"
        aria-label="Interactive map showing office location"
        loading="lazy"
      ></iframe>
    </div>

    <!-- Generic embed -->
    <embed
      src="/documents/presentation.pdf"
      type="application/pdf"
      width="600"
      height="400"
      aria-label="Product presentation PDF"
    >
  </section>

  <!-- Web Components -->
  <section class="components-section" aria-label="Custom web components">
    <h2>Custom Elements</h2>

    <custom-video-player
      src="/videos/demo.mp4"
      poster="/images/video-poster.jpg"
      controls="true"
      autoplay="false"
      aria-label="Custom video player component"
    >
      <p slot="fallback">Video player not supported in your browser.</p>
    </custom-video-player>

    <data-visualization
      type="chart"
      data-source="/api/analytics"
      refresh-interval="30000"
      aria-label="Real-time analytics dashboard"
    ></data-visualization>

    <image-gallery
      images='[
        {"src": "/images/1.jpg", "alt": "Image 1", "caption": "First image"},
        {"src": "/images/2.jpg", "alt": "Image 2", "caption": "Second image"}
      ]'
      layout="grid"
      lazy-loading="true"
    ></image-gallery>
  </section>
</body>
</html>
`;

      const result = await parserManager.parseFile('media.html', htmlCode);
      const extractor = new HTMLExtractor('html', 'media.html', htmlCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Image elements
      const heroImage = symbols.find(s => s.signature?.includes('src="/images/hero-image.jpg"'));
      expect(heroImage).toBeDefined();
      expect(heroImage?.kind).toBe(SymbolKind.Variable); // Media elements as variables
      expect(heroImage?.signature).toContain('loading="lazy"');
      expect(heroImage?.signature).toContain('decoding="async"');
      expect(heroImage?.signature).toContain('srcset=');
      expect(heroImage?.signature).toContain('sizes=');

      const dataUriImage = symbols.find(s => s.signature?.includes('data:image/svg+xml;base64'));
      expect(dataUriImage).toBeDefined();

      // Figure and figcaption
      const figureElement = symbols.find(s => s.name === 'figure');
      expect(figureElement).toBeDefined();

      const figcaptionElement = symbols.find(s => s.name === 'figcaption');
      expect(figcaptionElement).toBeDefined();

      const citeElement = symbols.find(s => s.name === 'cite');
      expect(citeElement).toBeDefined();

      // Picture and source elements
      const pictureElement = symbols.find(s => s.name === 'picture');
      expect(pictureElement).toBeDefined();

      const sourceElements = symbols.filter(s => s.name === 'source');
      expect(sourceElements.length).toBeGreaterThanOrEqual(5); // Picture sources + video/audio sources

      const webpSource = symbols.find(s => s.signature?.includes('type="image/webp"'));
      expect(webpSource).toBeDefined();

      const mediaSource = symbols.find(s => s.signature?.includes('media="(min-width: 1200px)"'));
      expect(mediaSource).toBeDefined();

      // Video element
      const videoElement = symbols.find(s => s.name === 'video');
      expect(videoElement).toBeDefined();
      expect(videoElement?.signature).toContain('controls');
      expect(videoElement?.signature).toContain('preload="metadata"');
      expect(videoElement?.signature).toContain('poster="/images/video-poster.jpg"');

      const mp4Source = symbols.find(s => s.signature?.includes('type="video/mp4"'));
      expect(mp4Source).toBeDefined();

      const webmSource = symbols.find(s => s.signature?.includes('type="video/webm"'));
      expect(webmSource).toBeDefined();

      // Track elements
      const trackElements = symbols.filter(s => s.name === 'track');
      expect(trackElements.length).toBe(4);

      const subtitlesTrack = symbols.find(s => s.signature?.includes('kind="subtitles"') && s.signature?.includes('srclang="en"'));
      expect(subtitlesTrack).toBeDefined();

      const captionsTrack = symbols.find(s => s.signature?.includes('kind="captions"'));
      expect(captionsTrack).toBeDefined();

      const descriptionsTrack = symbols.find(s => s.signature?.includes('kind="descriptions"'));
      expect(descriptionsTrack).toBeDefined();

      const defaultTrack = symbols.find(s => s.signature?.includes('default') && s.name === 'track');
      expect(defaultTrack).toBeDefined();

      // Audio element
      const audioElement = symbols.find(s => s.name === 'audio');
      expect(audioElement).toBeDefined();
      expect(audioElement?.signature).toContain('preload="none"');

      const mp3Source = symbols.find(s => s.signature?.includes('type="audio/mpeg"'));
      expect(mp3Source).toBeDefined();

      const oggSource = symbols.find(s => s.signature?.includes('type="audio/ogg"'));
      expect(oggSource).toBeDefined();

      // SVG element
      const svgElement = symbols.find(s => s.name === 'svg');
      expect(svgElement).toBeDefined();
      expect(svgElement?.signature).toContain('role="img"');
      expect(svgElement?.signature).toContain('aria-labelledby="chart-title chart-desc"');

      const titleElement = symbols.find(s => s.name === 'title' && s.signature?.includes('Sales Data Chart'));
      expect(titleElement).toBeDefined();

      const descElement = symbols.find(s => s.name === 'desc');
      expect(descElement).toBeDefined();

      // SVG defs and patterns
      const defsElement = symbols.find(s => s.name === 'defs');
      expect(defsElement).toBeDefined();

      const linearGradient = symbols.find(s => s.name === 'linearGradient');
      expect(linearGradient).toBeDefined();

      const patternElement = symbols.find(s => s.name === 'pattern');
      expect(patternElement).toBeDefined();

      const stopElements = symbols.filter(s => s.name === 'stop');
      expect(stopElements.length).toBe(2);

      // SVG shapes
      const rectElements = symbols.filter(s => s.name === 'rect');
      expect(rectElements.length).toBeGreaterThanOrEqual(6);

      const circleElement = symbols.find(s => s.name === 'circle');
      expect(circleElement).toBeDefined();

      const textElements = symbols.filter(s => s.name === 'text');
      expect(textElements.length).toBeGreaterThanOrEqual(4);

      // SVG animation
      const animateElement = symbols.find(s => s.name === 'animate');
      expect(animateElement).toBeDefined();
      expect(animateElement?.signature).toContain('attributeName="r"');
      expect(animateElement?.signature).toContain('repeatCount="indefinite"');

      // Object element
      const objectElement = symbols.find(s => s.name === 'object');
      expect(objectElement).toBeDefined();
      expect(objectElement?.signature).toContain('type="image/svg+xml"');

      // Canvas element
      const canvasElement = symbols.find(s => s.name === 'canvas');
      expect(canvasElement).toBeDefined();
      expect(canvasElement?.signature).toContain('role="img"');
      expect(canvasElement?.signature).toContain('aria-describedby="canvas-description"');

      // Iframe elements
      const iframeElements = symbols.filter(s => s.name === 'iframe');
      expect(iframeElements.length).toBe(2);

      const youtubeIframe = symbols.find(s => s.signature?.includes('youtube-nocookie.com'));
      expect(youtubeIframe).toBeDefined();
      expect(youtubeIframe?.signature).toContain('allowfullscreen');
      expect(youtubeIframe?.signature).toContain('loading="lazy"');

      const mapIframe = symbols.find(s => s.signature?.includes('openstreetmap.org'));
      expect(mapIframe).toBeDefined();

      // Embed element
      const embedElement = symbols.find(s => s.name === 'embed');
      expect(embedElement).toBeDefined();
      expect(embedElement?.signature).toContain('type="application/pdf"');

      // Custom elements
      const customVideoPlayer = symbols.find(s => s.name === 'custom-video-player');
      expect(customVideoPlayer).toBeDefined();
      expect(customVideoPlayer?.kind).toBe(SymbolKind.Class);
      expect(customVideoPlayer?.signature).toContain('controls="true"');

      const dataVisualization = symbols.find(s => s.name === 'data-visualization');
      expect(dataVisualization).toBeDefined();
      expect(dataVisualization?.signature).toContain('data-source="/api/analytics"');

      const imageGallery = symbols.find(s => s.name === 'image-gallery');
      expect(imageGallery).toBeDefined();
      expect(imageGallery?.signature).toContain('lazy-loading="true"');

      // Slot element
      const slotElement = symbols.find(s => s.signature?.includes('slot="fallback"'));
      expect(slotElement).toBeDefined();

      // Media-specific attributes
      const loadingLazy = symbols.filter(s => s.signature?.includes('loading="lazy"'));
      expect(loadingLazy.length).toBeGreaterThan(5);

      const decodingAsync = symbols.filter(s => s.signature?.includes('decoding="async"'));
      expect(decodingAsync.length).toBeGreaterThanOrEqual(3);

      const allowAttribute = symbols.find(s => s.signature?.includes('allow="accelerometer; autoplay'));
      expect(allowAttribute).toBeDefined();

      // Fallback content
      const videoFallback = symbols.find(s => s.signature?.includes('Your browser doesn\'t support HTML5 video'));
      expect(videoFallback).toBeDefined();

      const audioFallback = symbols.find(s => s.signature?.includes('Your browser doesn\'t support HTML5 audio'));
      expect(audioFallback).toBeDefined();

      const canvasFallback = symbols.find(s => s.signature?.includes('Canvas is not supported in your browser'));
      expect(canvasFallback).toBeDefined();
    });
  });
});