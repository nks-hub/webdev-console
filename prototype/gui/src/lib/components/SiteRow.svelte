<script lang="ts">
  import {
    Globe, FolderOpen, Lock, LockOpen,
    MoreHorizontal, Edit2, Trash2, ExternalLink, Terminal, ChevronRight
  } from 'lucide-svelte';
  import { invoke } from '@tauri-apps/api/core';
  // Note: open_url and open_terminal are Tauri commands in commands.rs

  // ---------------------------------------------------------------- //
  // Types                                                             //
  // ---------------------------------------------------------------- //

  export interface Site {
    id: string;
    domain: string;
    docRoot: string;
    phpVersion: string;
    ssl: boolean;
    status: 'active' | 'inactive' | 'error';
    server: 'apache' | 'nginx';
    createdAt?: string;
  }

  interface Props {
    site: Site;
    selected?: boolean;
    onSelect?: (site: Site) => void;
    onEdit?: (site: Site) => void;
    onDelete?: (site: Site) => void;
  }

  // ---------------------------------------------------------------- //
  // State                                                             //
  // ---------------------------------------------------------------- //

  const {
    site,
    selected = false,
    onSelect,
    onEdit,
    onDelete,
  }: Props = $props();

  let menuOpen = $state(false);
  let menuRef = $state<HTMLElement | null>(null);

  // ---------------------------------------------------------------- //
  // Derived                                                           //
  // ---------------------------------------------------------------- //

  const phpMajor = $derived(site.phpVersion.split('.').slice(0, 2).join(''));
  const phpColorVar = $derived(`--php-${phpMajor}`);

  const truncatedRoot = $derived(() => {
    const max = 36;
    if (site.docRoot.length <= max) return site.docRoot;
    const parts = site.docRoot.replace(/\\/g, '/').split('/');
    const tail = parts.slice(-2).join('/');
    return `…/${tail}`;
  });

  const statusDot = $derived({
    active:   'var(--status-running)',
    inactive: 'var(--status-stopped)',
    error:    'var(--status-warning)',
  }[site.status]);

  // ---------------------------------------------------------------- //
  // Actions                                                           //
  // ---------------------------------------------------------------- //

  function toggleMenu(e: MouseEvent) {
    e.stopPropagation();
    menuOpen = !menuOpen;
  }

  function handleRowClick() {
    onSelect?.(site);
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onSelect?.(site);
    }
  }

  async function openInBrowser() {
    menuOpen = false;
    const proto = site.ssl ? 'https' : 'http';
    await invoke('open_url', { url: `${proto}://${site.domain}` });
  }

  async function openTerminal() {
    menuOpen = false;
    await invoke('open_terminal', { cwd: site.docRoot });
  }

  function handleEdit(e: MouseEvent) {
    e.stopPropagation();
    menuOpen = false;
    onEdit?.(site);
  }

  function handleDelete(e: MouseEvent) {
    e.stopPropagation();
    menuOpen = false;
    onDelete?.(site);
  }

  // Close menu on outside click
  $effect(() => {
    if (!menuOpen) return;

    function handleOutsideClick(e: MouseEvent) {
      if (menuRef && !menuRef.contains(e.target as Node)) {
        menuOpen = false;
      }
    }

    document.addEventListener('click', handleOutsideClick);
    return () => document.removeEventListener('click', handleOutsideClick);
  });
</script>

<div
  class="site-row"
  class:selected
  role="row"
  tabindex="0"
  onclick={handleRowClick}
  onkeydown={handleKeydown}
  aria-selected={selected}
>
  <!-- Status dot + domain -->
  <div class="cell cell-domain">
    <div class="status-dot" style:background={statusDot} aria-hidden="true"></div>
    <Globe size={13} class="domain-icon" />
    <span class="domain-name">{site.domain}</span>
  </div>

  <!-- Document root -->
  <div class="cell cell-root">
    <FolderOpen size={12} class="icon-muted" />
    <span class="doc-root font-mono" title={site.docRoot}>{truncatedRoot()}</span>
  </div>

  <!-- PHP version badge -->
  <div class="cell cell-php">
    <span class="php-badge" style:background="color-mix(in srgb, {`var(${phpColorVar})`} 15%, transparent)" style:color={`var(${phpColorVar})`} style:border-color="color-mix(in srgb, {`var(${phpColorVar})`} 30%, transparent)">
      PHP {site.phpVersion}
    </span>
  </div>

  <!-- SSL indicator -->
  <div class="cell cell-ssl" aria-label={site.ssl ? 'SSL active' : 'No SSL'}>
    {#if site.ssl}
      <Lock size={13} class="ssl-active" />
      <span class="ssl-label ssl-active">HTTPS</span>
    {:else}
      <LockOpen size={13} class="ssl-none" />
      <span class="ssl-label ssl-none">HTTP</span>
    {/if}
  </div>

  <!-- Chevron (selection indicator) -->
  <div class="cell cell-chevron" aria-hidden="true">
    <ChevronRight size={14} class="chevron" />
  </div>

  <!-- Actions menu -->
  <div class="cell cell-actions" bind:this={menuRef}>
    <button
      class="btn-menu"
      onclick={toggleMenu}
      aria-label="Actions for {site.domain}"
      aria-haspopup="menu"
      aria-expanded={menuOpen}
    >
      <MoreHorizontal size={15} />
    </button>

    {#if menuOpen}
      <div class="dropdown-menu" role="menu">
        <button class="menu-item" role="menuitem" onclick={openInBrowser}>
          <ExternalLink size={13} />
          Open in browser
        </button>
        <button class="menu-item" role="menuitem" onclick={openTerminal}>
          <Terminal size={13} />
          Open terminal here
        </button>
        <div class="menu-separator" role="separator"></div>
        <button class="menu-item" role="menuitem" onclick={handleEdit}>
          <Edit2 size={13} />
          Edit site
        </button>
        <button class="menu-item menu-item-danger" role="menuitem" onclick={handleDelete}>
          <Trash2 size={13} />
          Delete site
        </button>
      </div>
    {/if}
  </div>
</div>

<style>
  .site-row {
    display: grid;
    grid-template-columns: 1fr auto auto auto auto 32px;
    align-items: center;
    gap: var(--space-3);
    padding: var(--space-2) var(--space-4);
    border-bottom: 1px solid var(--border-default);
    cursor: pointer;
    transition: background var(--transition-fast);
    outline: none;
    min-height: 44px;
  }

  .site-row:hover {
    background: var(--bg-hover);
  }

  .site-row:focus-visible {
    box-shadow: inset 0 0 0 2px var(--accent-blue);
  }

  .site-row.selected {
    background: var(--bg-active);
  }

  /* Cells */
  .cell {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    min-width: 0;
  }

  .cell-domain {
    min-width: 160px;
    flex: 1;
  }

  .cell-root {
    min-width: 140px;
    flex: 1;
    color: var(--text-muted);
  }

  .status-dot {
    width: 7px;
    height: 7px;
    border-radius: var(--radius-full);
    flex-shrink: 0;
  }

  :global(.domain-icon) {
    color: var(--text-muted);
    flex-shrink: 0;
  }

  .domain-name {
    font-size: var(--font-size-sm);
    font-weight: 500;
    color: var(--text-primary);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  :global(.icon-muted) {
    color: var(--text-muted);
    flex-shrink: 0;
  }

  .doc-root {
    font-size: var(--font-size-xs);
    color: var(--text-secondary);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* PHP badge */
  .php-badge {
    font-family: var(--font-mono);
    font-size: var(--font-size-xs);
    font-weight: 600;
    padding: 2px 7px;
    border-radius: var(--radius-sm);
    border: 1px solid;
    white-space: nowrap;
  }

  /* SSL */
  .cell-ssl {
    gap: var(--space-1);
    white-space: nowrap;
  }

  .ssl-label {
    font-size: var(--font-size-xs);
    font-weight: 500;
  }

  :global(.ssl-active) { color: var(--status-running) !important; }
  :global(.ssl-none)   { color: var(--text-muted) !important; }

  /* Chevron */
  .cell-chevron {
    justify-content: center;
    transition: transform var(--transition-fast);
  }

  .selected .cell-chevron {
    transform: translateX(2px);
  }

  :global(.chevron) {
    color: var(--text-muted);
    transition: color var(--transition-fast);
  }

  .site-row:hover :global(.chevron),
  .site-row.selected :global(.chevron) {
    color: var(--text-secondary);
  }

  /* Menu button */
  .btn-menu {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    border: none;
    background: transparent;
    color: var(--text-muted);
    border-radius: var(--radius-md);
    cursor: pointer;
    transition: background var(--transition-fast), color var(--transition-fast);
    opacity: 0;
  }

  .site-row:hover .btn-menu,
  .site-row:focus-within .btn-menu {
    opacity: 1;
  }

  .btn-menu:hover {
    background: var(--bg-elevated);
    color: var(--text-primary);
  }

  /* Dropdown */
  .cell-actions {
    position: relative;
  }

  .dropdown-menu {
    position: absolute;
    right: 0;
    top: calc(100% + 4px);
    background: var(--bg-elevated);
    border: 1px solid var(--border-strong);
    border-radius: var(--radius-lg);
    box-shadow: var(--shadow-lg);
    padding: var(--space-1) 0;
    min-width: 180px;
    z-index: 100;
  }

  .menu-item {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    width: 100%;
    padding: var(--space-2) var(--space-3);
    border: none;
    background: transparent;
    color: var(--text-secondary);
    font-size: var(--font-size-sm);
    font-family: var(--font-ui);
    cursor: pointer;
    text-align: left;
    transition: background var(--transition-fast), color var(--transition-fast);
  }

  .menu-item:hover {
    background: var(--bg-hover);
    color: var(--text-primary);
  }

  .menu-item-danger:hover {
    background: var(--status-stopped-bg);
    color: var(--status-stopped);
  }

  .menu-separator {
    height: 1px;
    background: var(--border-default);
    margin: var(--space-1) 0;
  }
</style>
