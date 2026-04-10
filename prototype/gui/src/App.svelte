<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import {
    LayoutDashboard, Globe, Cpu, Database,
    ShieldCheck, Terminal, Settings, ChevronLeft,
    ChevronRight, Search, Sun, Moon, Activity
  } from 'lucide-svelte';
  import { connectToDaemon, disconnectFromDaemon, services } from '$lib/stores/services';
  import './app.css';

  // ---------------------------------------------------------------- //
  // Navigation config                                                 //
  // ---------------------------------------------------------------- //

  type NavItem = {
    id: string;
    label: string;
    icon: typeof LayoutDashboard;
    badge?: () => string | null;
  };

  const NAV_ITEMS: NavItem[] = [
    { id: 'dashboard', label: 'Dashboard',  icon: LayoutDashboard,
      badge: () => services.anyRunning ? `${services.runningCount}` : null },
    { id: 'sites',     label: 'Sites',      icon: Globe },
    { id: 'php',       label: 'PHP',        icon: Cpu },
    { id: 'database',  label: 'Database',   icon: Database },
    { id: 'ssl',       label: 'SSL',        icon: ShieldCheck },
    { id: 'terminal',  label: 'Terminal',   icon: Terminal },
    { id: 'settings',  label: 'Settings',   icon: Settings },
  ];

  // ---------------------------------------------------------------- //
  // State                                                             //
  // ---------------------------------------------------------------- //

  let activeSection = $state('dashboard');
  let sidebarExpanded = $state(true);
  let theme = $state<'dark' | 'light'>('dark');
  let commandPaletteOpen = $state(false);
  let commandQuery = $state('');
  let commandInputRef = $state<HTMLInputElement | null>(null);

  // ---------------------------------------------------------------- //
  // Theme                                                             //
  // ---------------------------------------------------------------- //

  $effect(() => {
    document.documentElement.setAttribute('data-theme', theme);
  });

  function toggleTheme() {
    theme = theme === 'dark' ? 'light' : 'dark';
  }

  // ---------------------------------------------------------------- //
  // Command palette                                                   //
  // ---------------------------------------------------------------- //

  type Command = { id: string; label: string; description: string; action: () => void };

  const COMMANDS: Command[] = [
    { id: 'goto-dashboard', label: 'Go to Dashboard',  description: 'Navigation', action: () => { activeSection = 'dashboard'; commandPaletteOpen = false; } },
    { id: 'goto-sites',     label: 'Go to Sites',      description: 'Navigation', action: () => { activeSection = 'sites'; commandPaletteOpen = false; } },
    { id: 'goto-php',       label: 'Go to PHP Manager', description: 'Navigation', action: () => { activeSection = 'php'; commandPaletteOpen = false; } },
    { id: 'goto-db',        label: 'Go to Database',   description: 'Navigation', action: () => { activeSection = 'database'; commandPaletteOpen = false; } },
    { id: 'goto-ssl',       label: 'Go to SSL',        description: 'Navigation', action: () => { activeSection = 'ssl'; commandPaletteOpen = false; } },
    { id: 'goto-terminal',  label: 'Open Terminal',    description: 'Navigation', action: () => { activeSection = 'terminal'; commandPaletteOpen = false; } },
    { id: 'toggle-theme',   label: 'Toggle Dark/Light Theme', description: 'Appearance', action: () => { toggleTheme(); commandPaletteOpen = false; } },
    { id: 'toggle-sidebar', label: 'Toggle Sidebar',  description: 'Appearance', action: () => { sidebarExpanded = !sidebarExpanded; commandPaletteOpen = false; } },
  ];

  const filteredCommands = $derived(() => {
    if (!commandQuery.trim()) return COMMANDS;
    const q = commandQuery.toLowerCase();
    return COMMANDS.filter(c =>
      c.label.toLowerCase().includes(q) || c.description.toLowerCase().includes(q)
    );
  });

  function openCommandPalette() {
    commandPaletteOpen = true;
    commandQuery = '';
  }

  function closeCommandPalette() {
    commandPaletteOpen = false;
    commandQuery = '';
  }

  $effect(() => {
    if (commandPaletteOpen) {
      // Focus input after DOM update
      setTimeout(() => commandInputRef?.focus(), 10);
    }
  });

  // ---------------------------------------------------------------- //
  // Keyboard shortcuts                                                //
  // ---------------------------------------------------------------- //

  function handleGlobalKeydown(e: KeyboardEvent) {
    // Ctrl+K — open command palette
    if (e.ctrlKey && e.key === 'k') {
      e.preventDefault();
      commandPaletteOpen ? closeCommandPalette() : openCommandPalette();
      return;
    }

    // Escape — close palette
    if (e.key === 'Escape' && commandPaletteOpen) {
      closeCommandPalette();
    }
  }

  // ---------------------------------------------------------------- //
  // Lifecycle                                                         //
  // ---------------------------------------------------------------- //

  onMount(async () => {
    await connectToDaemon();
  });

  onDestroy(() => {
    disconnectFromDaemon();
  });
</script>

<svelte:window onkeydown={handleGlobalKeydown} />

<!-- Root layout -->
<div class="app-shell">

  <!-- Sidebar -->
  <aside class="sidebar" class:expanded={sidebarExpanded} aria-label="Main navigation">

    <!-- Branding -->
    <div class="sidebar-brand">
      <div class="brand-logo" aria-hidden="true">
        <Activity size={20} />
      </div>
      {#if sidebarExpanded}
        <span class="brand-name">NKS WebDev Console</span>
      {/if}
    </div>

    <!-- Nav items -->
    <nav class="sidebar-nav">
      {#each NAV_ITEMS as item (item.id)}
        {@const badge = item.badge?.()}
        <button
          class="nav-item"
          class:active={activeSection === item.id}
          onclick={() => { activeSection = item.id; }}
          title={sidebarExpanded ? undefined : item.label}
          aria-label={item.label}
          aria-current={activeSection === item.id ? 'page' : undefined}
        >
          <div class="nav-icon">
            <item.icon size={17} />
            {#if badge && !sidebarExpanded}
              <span class="nav-badge-dot" aria-hidden="true"></span>
            {/if}
          </div>
          {#if sidebarExpanded}
            <span class="nav-label">{item.label}</span>
            {#if badge}
              <span class="nav-badge" aria-label="{badge} services running">{badge}</span>
            {/if}
          {/if}
          {#if activeSection === item.id}
            <div class="nav-active-bar" aria-hidden="true"></div>
          {/if}
        </button>
      {/each}
    </nav>

    <!-- Sidebar footer -->
    <div class="sidebar-footer">
      <!-- Connection status -->
      <div class="connection-indicator" title={services.connected ? 'Daemon connected' : 'Daemon disconnected'}>
        <div class="conn-dot" class:connected={services.connected}></div>
        {#if sidebarExpanded}
          <span class="conn-label">{services.connected ? 'Connected' : 'Offline'}</span>
        {/if}
      </div>

      <!-- Theme toggle -->
      <button class="btn-icon-sm" onclick={toggleTheme} aria-label="Toggle theme" title="Toggle theme">
        {#if theme === 'dark'}
          <Sun size={14} />
        {:else}
          <Moon size={14} />
        {/if}
      </button>

      <!-- Collapse toggle -->
      <button
        class="btn-icon-sm"
        onclick={() => { sidebarExpanded = !sidebarExpanded; }}
        aria-label={sidebarExpanded ? 'Collapse sidebar' : 'Expand sidebar'}
        title={sidebarExpanded ? 'Collapse' : 'Expand'}
      >
        {#if sidebarExpanded}
          <ChevronLeft size={14} />
        {:else}
          <ChevronRight size={14} />
        {/if}
      </button>
    </div>

  </aside>

  <!-- Content area -->
  <main class="content-area" id="main-content">

    <!-- Topbar -->
    <header class="topbar">
      <h1 class="page-title">
        {NAV_ITEMS.find(n => n.id === activeSection)?.label ?? 'NKS WebDev Console'}
      </h1>

      <div class="topbar-actions">
        <button
          class="cmd-trigger"
          onclick={openCommandPalette}
          aria-label="Open command palette (Ctrl+K)"
          title="Command palette  Ctrl+K"
        >
          <Search size={13} />
          <span class="cmd-trigger-label">Quick actions…</span>
          <kbd class="kbd">Ctrl K</kbd>
        </button>
      </div>
    </header>

    <!-- Section content (swap based on activeSection) -->
    <div class="section-content">
      {#if activeSection === 'dashboard'}
        <slot name="dashboard">
          <div class="placeholder-section">
            <p class="placeholder-text">Dashboard — import and render ServiceCard components here.</p>
          </div>
        </slot>
      {:else if activeSection === 'sites'}
        <slot name="sites">
          <div class="placeholder-section">
            <p class="placeholder-text">Sites Manager — render SiteRow components inside a table here.</p>
          </div>
        </slot>
      {:else}
        <div class="placeholder-section">
          <p class="placeholder-text">{NAV_ITEMS.find(n => n.id === activeSection)?.label} — coming soon.</p>
        </div>
      {/if}
    </div>

  </main>
</div>

<!-- Command palette overlay -->
{#if commandPaletteOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="palette-backdrop" onclick={closeCommandPalette}></div>

  <div class="palette" role="dialog" aria-label="Command palette" aria-modal="true">
    <div class="palette-search">
      <Search size={15} class="palette-search-icon" />
      <input
        bind:this={commandInputRef}
        bind:value={commandQuery}
        type="text"
        class="palette-input"
        placeholder="Type a command…"
        autocomplete="off"
        spellcheck="false"
      />
      <kbd class="kbd">Esc</kbd>
    </div>

    <ul class="palette-list" role="listbox">
      {#each filteredCommands() as cmd (cmd.id)}
        <li
          class="palette-item"
          role="option"
          aria-selected="false"
          onclick={() => cmd.action()}
          onkeydown={(e) => { if (e.key === 'Enter') cmd.action(); }}
          tabindex="0"
        >
          <span class="palette-item-label">{cmd.label}</span>
          <span class="palette-item-desc">{cmd.description}</span>
        </li>
      {:else}
        <li class="palette-empty">No commands match "{commandQuery}"</li>
      {/each}
    </ul>
  </div>
{/if}

<style>
  /* ---------------------------------------------------------------- */
  /* Shell layout                                                      */
  /* ---------------------------------------------------------------- */
  .app-shell {
    display: flex;
    height: 100vh;
    width: 100vw;
    overflow: hidden;
    background: var(--bg-base);
  }

  /* ---------------------------------------------------------------- */
  /* Sidebar                                                           */
  /* ---------------------------------------------------------------- */
  .sidebar {
    width: var(--sidebar-width-collapsed);
    background: var(--sidebar-bg);
    border-right: 1px solid var(--border-default);
    display: flex;
    flex-direction: column;
    flex-shrink: 0;
    transition: width var(--transition-normal);
    overflow: hidden;
  }

  .sidebar.expanded {
    width: var(--sidebar-width-expanded);
  }

  /* Brand */
  .sidebar-brand {
    display: flex;
    align-items: center;
    gap: var(--space-3);
    padding: var(--space-4) var(--space-4) var(--space-3);
    border-bottom: 1px solid var(--border-default);
    height: 52px;
    flex-shrink: 0;
  }

  .brand-logo {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    background: var(--accent-blue-dim);
    border-radius: var(--radius-md);
    color: var(--accent-blue);
    flex-shrink: 0;
  }

  .brand-name {
    font-size: var(--font-size-md);
    font-weight: 700;
    color: var(--text-primary);
    letter-spacing: -0.01em;
    white-space: nowrap;
  }

  /* Nav */
  .sidebar-nav {
    flex: 1;
    display: flex;
    flex-direction: column;
    padding: var(--space-2) 0;
    overflow-y: auto;
    overflow-x: hidden;
  }

  .nav-item {
    position: relative;
    display: flex;
    align-items: center;
    gap: var(--space-3);
    padding: 0 var(--space-4);
    height: 36px;
    border: none;
    background: transparent;
    color: var(--text-secondary);
    cursor: pointer;
    transition: background var(--transition-fast), color var(--transition-fast);
    text-align: left;
    white-space: nowrap;
    overflow: hidden;
  }

  .nav-item:hover {
    background: var(--bg-hover);
    color: var(--text-primary);
  }

  .nav-item.active {
    background: var(--sidebar-item-active-bg);
    color: var(--text-primary);
  }

  .nav-active-bar {
    position: absolute;
    left: 0;
    top: 25%;
    bottom: 25%;
    width: 3px;
    background: var(--sidebar-item-active-border);
    border-radius: 0 var(--radius-sm) var(--radius-sm) 0;
  }

  .nav-icon {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    flex-shrink: 0;
  }

  .nav-badge-dot {
    position: absolute;
    top: -2px;
    right: -2px;
    width: 6px;
    height: 6px;
    border-radius: var(--radius-full);
    background: var(--accent-blue);
  }

  .nav-label {
    font-size: var(--font-size-sm);
    font-weight: 500;
    flex: 1;
    min-width: 0;
  }

  .nav-badge {
    font-size: var(--font-size-xs);
    font-weight: 600;
    background: var(--accent-blue-dim);
    color: var(--accent-blue);
    border-radius: var(--radius-full);
    padding: 1px 6px;
    min-width: 18px;
    text-align: center;
  }

  /* Sidebar footer */
  .sidebar-footer {
    display: flex;
    align-items: center;
    gap: var(--space-1);
    padding: var(--space-3) var(--space-3);
    border-top: 1px solid var(--border-default);
    flex-shrink: 0;
  }

  .connection-indicator {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    flex: 1;
    min-width: 0;
    overflow: hidden;
  }

  .conn-dot {
    width: 7px;
    height: 7px;
    border-radius: var(--radius-full);
    background: var(--status-stopped);
    flex-shrink: 0;
    transition: background var(--transition-normal);
  }

  .conn-dot.connected {
    background: var(--status-running);
  }

  .conn-label {
    font-size: var(--font-size-xs);
    color: var(--text-muted);
    white-space: nowrap;
  }

  .btn-icon-sm {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    border: none;
    background: transparent;
    color: var(--text-muted);
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition: background var(--transition-fast), color var(--transition-fast);
    flex-shrink: 0;
  }

  .btn-icon-sm:hover {
    background: var(--bg-elevated);
    color: var(--text-secondary);
  }

  /* ---------------------------------------------------------------- */
  /* Content area                                                      */
  /* ---------------------------------------------------------------- */
  .content-area {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-width: 0;
    overflow: hidden;
  }

  .topbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0 var(--space-6);
    height: 52px;
    border-bottom: 1px solid var(--border-default);
    flex-shrink: 0;
    gap: var(--space-4);
  }

  .page-title {
    font-size: var(--font-size-md);
    font-weight: 600;
    color: var(--text-primary);
    margin: 0;
  }

  .topbar-actions {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  /* Command trigger button */
  .cmd-trigger {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    padding: var(--space-1) var(--space-3);
    height: 30px;
    background: var(--bg-elevated);
    border: 1px solid var(--border-default);
    border-radius: var(--radius-md);
    color: var(--text-muted);
    font-size: var(--font-size-sm);
    font-family: var(--font-ui);
    cursor: pointer;
    transition: border-color var(--transition-fast), background var(--transition-fast);
    gap: var(--space-2);
  }

  .cmd-trigger:hover {
    border-color: var(--border-strong);
    background: var(--bg-overlay);
    color: var(--text-secondary);
  }

  .cmd-trigger-label {
    color: var(--text-muted);
  }

  .kbd {
    font-family: var(--font-mono);
    font-size: 10px;
    padding: 1px 5px;
    background: var(--bg-surface);
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-sm);
    color: var(--text-muted);
  }

  .section-content {
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden;
    padding: var(--space-6);
  }

  .placeholder-section {
    display: flex;
    align-items: center;
    justify-content: center;
    height: 200px;
    border: 1px dashed var(--border-default);
    border-radius: var(--radius-lg);
  }

  .placeholder-text {
    color: var(--text-muted);
    font-size: var(--font-size-sm);
  }

  /* ---------------------------------------------------------------- */
  /* Command palette                                                   */
  /* ---------------------------------------------------------------- */
  .palette-backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.55);
    backdrop-filter: blur(4px);
    z-index: 1000;
  }

  .palette {
    position: fixed;
    top: 20%;
    left: 50%;
    transform: translateX(-50%);
    width: min(520px, calc(100vw - 32px));
    background: var(--bg-elevated);
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-xl);
    box-shadow: var(--shadow-lg);
    z-index: 1001;
    overflow: hidden;
  }

  .palette-search {
    display: flex;
    align-items: center;
    gap: var(--space-3);
    padding: var(--space-3) var(--space-4);
    border-bottom: 1px solid var(--border-default);
  }

  :global(.palette-search-icon) {
    color: var(--text-muted);
    flex-shrink: 0;
  }

  .palette-input {
    flex: 1;
    border: none;
    background: transparent;
    color: var(--text-primary);
    font-size: var(--font-size-base);
    font-family: var(--font-ui);
    outline: none;
  }

  .palette-input::placeholder {
    color: var(--text-muted);
  }

  .palette-list {
    list-style: none;
    margin: 0;
    padding: var(--space-1) 0;
    max-height: 320px;
    overflow-y: auto;
  }

  .palette-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--space-2) var(--space-4);
    cursor: pointer;
    transition: background var(--transition-fast);
    outline: none;
  }

  .palette-item:hover,
  .palette-item:focus {
    background: var(--bg-hover);
  }

  .palette-item-label {
    font-size: var(--font-size-sm);
    color: var(--text-primary);
  }

  .palette-item-desc {
    font-size: var(--font-size-xs);
    color: var(--text-muted);
  }

  .palette-empty {
    padding: var(--space-4);
    text-align: center;
    color: var(--text-muted);
    font-size: var(--font-size-sm);
  }
</style>
