<template>
  <div class="help-page">
    <!-- Page header -->
    <div class="page-header help-page-header">
      <h1 class="page-title">{{ t('help.title') }}</h1>
      <el-button type="primary" size="small" @click="openWizard">
        &#9654; {{ t('help.startGettingStarted') }}
      </el-button>
    </div>

    <!-- Breadcrumb -->
    <div class="help-breadcrumb" v-if="activeTopic">
      <span class="bc-item bc-link" @click="clearTopic">{{ t('help.breadcrumbRoot') }}</span>
      <span class="bc-sep">/</span>
      <span class="bc-item bc-link" @click="clearTopic">{{ t(`help.groups.${activeGroup?.key}`) }}</span>
      <span class="bc-sep">/</span>
      <span class="bc-item bc-current">{{ t(`help.topics.${activeTopic.key}.title`) }}</span>
    </div>
    <div class="help-breadcrumb" v-else>
      <span class="bc-item bc-current">{{ t('help.breadcrumbRoot') }}</span>
    </div>

    <div class="help-body">
      <!-- Sidebar -->
      <aside class="help-sidebar">
        <div class="help-search">
          <el-input
            v-model="searchQuery"
            size="small"
            :placeholder="t('help.searchPlaceholder')"
            clearable
          >
            <template #prefix><el-icon><Search /></el-icon></template>
          </el-input>
        </div>

        <nav class="help-nav" v-if="!searchQuery.trim()">
          <div v-for="group in groups" :key="group.key" class="help-group">
            <button
              class="help-group-header"
              :class="{ open: openGroups.has(group.key) }"
              @click="toggleGroup(group.key)"
              :aria-expanded="openGroups.has(group.key)"
            >
              <el-icon class="group-icon"><component :is="group.icon" /></el-icon>
              <span>{{ t(`help.groups.${group.key}`) }}</span>
              <el-icon class="group-chevron"><ArrowDown /></el-icon>
            </button>
            <div v-show="openGroups.has(group.key)" class="help-group-items">
              <button
                v-for="topic in group.topics"
                :key="topic.key"
                class="help-nav-item"
                :class="{ active: activeTopic?.key === topic.key }"
                @click="selectTopic(group, topic)"
              >
                {{ t(`help.topics.${topic.key}.title`) }}
              </button>
            </div>
          </div>
        </nav>

        <!-- Search results flat list -->
        <nav class="help-nav" v-else>
          <template v-if="searchResults.length">
            <button
              v-for="result in searchResults"
              :key="result.topic.key"
              class="help-nav-item"
              :class="{ active: activeTopic?.key === result.topic.key }"
              @click="selectTopic(result.group, result.topic)"
            >
              <span class="search-result-group">{{ t(`help.groups.${result.group.key}`) }}</span>
              {{ t(`help.topics.${result.topic.key}.title`) }}
            </button>
          </template>
          <div v-else class="help-no-results">
            {{ t('help.noResults', { q: searchQuery.trim() }) }}
          </div>
        </nav>
      </aside>

      <!-- Content pane -->
      <article class="help-content" ref="contentRef">
        <template v-if="activeTopic">
          <!-- Video placeholder (task 32) -->
          <div class="help-video-placeholder">
            <div class="video-inner">
              <span class="video-play-icon">&#9654;</span>
              <span class="video-label">{{ t('help.comingSoonVideo') }}</span>
            </div>
          </div>

          <!-- In-page anchor nav for long topics -->
          <nav v-if="contentAnchors.length > 1" class="help-anchor-nav" aria-label="Jump to">
            <span class="anchor-nav-label">Jump to:</span>
            <a
              v-for="anchor in contentAnchors"
              :key="anchor.id"
              :href="`#${anchor.id}`"
              class="anchor-nav-link"
              @click.prevent="scrollToAnchor(anchor.id)"
            >{{ anchor.text }}</a>
          </nav>

          <h2 class="help-section-title">{{ t(`help.topics.${activeTopic.key}.title`) }}</h2>
          <div class="help-prose" v-html="t(`help.topics.${activeTopic.key}.body`)" ref="proseRef" />
        </template>

        <div v-else class="help-welcome">
          <div class="welcome-icon">?</div>
          <h2 class="welcome-title">{{ t('help.title') }}</h2>
          <p class="welcome-hint">{{ t('help.searchPlaceholder') }}</p>
          <div class="welcome-groups">
            <button
              v-for="group in groups"
              :key="group.key"
              class="welcome-group-btn"
              @click="selectFirstInGroup(group)"
            >
              <el-icon><component :is="group.icon" /></el-icon>
              {{ t(`help.groups.${group.key}`) }}
            </button>
          </div>
        </div>
      </article>
    </div>

    <GettingStartedWizard ref="wizardRef" />
  </div>
</template>

<script setup lang="ts">
import { ref, computed, nextTick, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Search, House, Monitor, DataLine, Lock, Connection, Document,
  Setting, Box, Download, DataBoard, Tools, Histogram, ArrowDown,
  Menu, Star,
} from '@element-plus/icons-vue'
import GettingStartedWizard from '../shared/GettingStartedWizard.vue'

const { t } = useI18n()

interface Topic {
  key: string
}

interface Group {
  key: string
  icon: unknown
  topics: Topic[]
}

const groups: Group[] = [
  {
    key: 'gettingStarted',
    icon: Star,
    topics: [
      { key: 'overview' },
      { key: 'firstSite' },
    ],
  },
  {
    key: 'sites',
    icon: Monitor,
    topics: [
      { key: 'sitesSimple' },
      { key: 'sitesAdvanced' },
    ],
  },
  {
    key: 'services',
    icon: Menu,
    topics: [
      { key: 'databases' },
      { key: 'ssl' },
      { key: 'composer' },
      { key: 'cloudflare' },
      { key: 'hosts' },
    ],
  },
  {
    key: 'advanced',
    icon: Tools,
    topics: [
      { key: 'plugins' },
      { key: 'mcp' },
      { key: 'shortcuts' },
      { key: 'binaries' },
      { key: 'metrics' },
    ],
  },
  {
    key: 'settings',
    icon: Setting,
    topics: [
      { key: 'settings' },
    ],
  },
]

const openGroups = ref<Set<string>>(new Set(['gettingStarted']))
const activeTopic = ref<Topic | null>(null)
const activeGroup = ref<Group | null>(null)
const searchQuery = ref('')
const contentRef = ref<HTMLElement | null>(null)
const proseRef = ref<HTMLElement | null>(null)
const wizardRef = ref<InstanceType<typeof GettingStartedWizard> | null>(null)

interface SearchResult {
  group: Group
  topic: Topic
}

const searchResults = computed<SearchResult[]>(() => {
  const q = searchQuery.value.trim().toLowerCase()
  if (!q) return []
  const results: SearchResult[] = []
  for (const group of groups) {
    for (const topic of group.topics) {
      const titleKey = `help.topics.${topic.key}.title`
      const bodyKey = `help.topics.${topic.key}.body`
      const titleText = t(titleKey).toLowerCase()
      const bodyText = t(bodyKey).replace(/<[^>]+>/g, '').toLowerCase()
      if (titleText.includes(q) || bodyText.includes(q)) {
        results.push({ group, topic })
      }
    }
  }
  return results
})

const contentAnchors = computed<Array<{ id: string; text: string }>>(() => {
  if (!activeTopic.value) return []
  const body = t(`help.topics.${activeTopic.value.key}.body`)
  const matches = [...body.matchAll(/id='([^']+)'[^>]*>([^<]+)</g)]
  return matches.map((m) => ({ id: m[1], text: m[2] }))
})

function toggleGroup(key: string) {
  if (openGroups.value.has(key)) {
    openGroups.value.delete(key)
  } else {
    openGroups.value.add(key)
  }
}

function selectTopic(group: Group, topic: Topic) {
  activeGroup.value = group
  activeTopic.value = topic
  openGroups.value.add(group.key)
  searchQuery.value = ''
  nextTick(() => contentRef.value?.scrollTo({ top: 0, behavior: 'smooth' }))
}

function selectFirstInGroup(group: Group) {
  if (group.topics.length > 0) {
    selectTopic(group, group.topics[0])
  }
}

function clearTopic() {
  activeTopic.value = null
  activeGroup.value = null
}

function scrollToAnchor(id: string) {
  const el = proseRef.value?.querySelector(`#${id}`)
  if (el) {
    el.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }
}

function openWizard() {
  wizardRef.value?.open()
}

onMounted(() => {
  selectTopic(groups[0], groups[0].topics[0])
})
</script>

<style scoped>
.help-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.help-page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

/* Breadcrumb */
.help-breadcrumb {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 4px 0 10px;
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  flex-shrink: 0;
}

.bc-sep { color: var(--wdc-border); }

.bc-link {
  cursor: pointer;
  color: var(--wdc-accent);
  transition: opacity 0.15s;
}

.bc-link:hover { opacity: 0.75; }
.bc-current { color: var(--wdc-text-2); }

/* Body layout */
.help-body {
  display: flex;
  gap: 0;
  flex: 1;
  min-height: 0;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}

/* Sidebar */
.help-sidebar {
  width: 220px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
  border-right: 1px solid var(--wdc-border);
  background: var(--wdc-surface);
  overflow-y: auto;
  padding: 10px 6px;
}

.help-search { padding: 0 4px; }

.help-nav {
  display: flex;
  flex-direction: column;
  gap: 2px;
  flex: 1;
}

/* Accordion group */
.help-group { display: flex; flex-direction: column; }

.help-group-header {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 7px 8px;
  background: transparent;
  border: none;
  border-radius: 6px;
  color: var(--wdc-text);
  font-size: 0.8rem;
  font-weight: 600;
  text-align: left;
  cursor: pointer;
  transition: background 0.15s;
  width: 100%;
}

.help-group-header:hover { background: var(--wdc-surface-2); }

.group-icon { font-size: 14px; color: var(--wdc-text-2); flex-shrink: 0; }

.group-chevron {
  margin-left: auto;
  font-size: 11px;
  color: var(--wdc-text-3);
  transition: transform 0.2s;
  flex-shrink: 0;
}

.help-group-header.open .group-chevron { transform: rotate(180deg); }

.help-group-items {
  display: flex;
  flex-direction: column;
  padding-left: 22px;
  gap: 1px;
  margin-bottom: 4px;
}

.help-nav-item {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  padding: 5px 10px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 5px;
  color: var(--wdc-text-2);
  font-size: 0.82rem;
  text-align: left;
  cursor: pointer;
  transition: background 0.15s;
  width: 100%;
}

.help-nav-item:hover { background: var(--wdc-surface-2); color: var(--wdc-text); }

.help-nav-item.active {
  background: rgba(86, 194, 255, 0.08);
  border-color: var(--wdc-border);
  color: var(--wdc-accent);
  font-weight: 600;
}

.search-result-group {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--wdc-text-3);
  margin-bottom: 1px;
}

.help-no-results {
  padding: 16px 10px;
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  font-style: italic;
}

/* Content pane */
.help-content {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 20px 24px 40px;
  background: var(--wdc-surface);
}

/* Video placeholder */
.help-video-placeholder {
  width: 100%;
  max-width: 640px;
  aspect-ratio: 16 / 9;
  background: var(--wdc-surface-2);
  border: 2px dashed var(--wdc-border);
  border-radius: var(--wdc-radius);
  display: flex;
  align-items: center;
  justify-content: center;
  margin-bottom: 20px;
}

.video-inner {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  color: var(--wdc-text-3);
}

.video-play-icon {
  font-size: 2rem;
  opacity: 0.4;
}

.video-label {
  font-size: 0.82rem;
  font-style: italic;
}

/* Anchor nav */
.help-anchor-nav {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 10px;
  padding: 8px 12px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  font-size: 0.78rem;
  margin-bottom: 16px;
  max-width: 640px;
}

.anchor-nav-label {
  color: var(--wdc-text-3);
  font-weight: 600;
  flex-shrink: 0;
}

.anchor-nav-link {
  color: var(--wdc-accent);
  text-decoration: none;
  transition: opacity 0.15s;
}

.anchor-nav-link:hover { text-decoration: underline; opacity: 0.8; }

/* Section title */
.help-section-title {
  font-size: 1.4rem;
  font-weight: 700;
  margin: 0 0 16px;
  color: var(--wdc-text);
}

/* Prose */
.help-prose {
  font-size: 0.92rem;
  line-height: 1.65;
  color: var(--wdc-text);
  max-width: 680px;
}

.help-prose :deep(h3) {
  margin: 22px 0 8px;
  font-size: 1rem;
  font-weight: 600;
  color: var(--wdc-text);
  padding-top: 4px;
}

.help-prose :deep(p) { margin: 0 0 10px; }

.help-prose :deep(code) {
  background: var(--wdc-surface-2);
  padding: 2px 6px;
  border-radius: 4px;
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
  color: var(--wdc-accent);
}

.help-prose :deep(a) { color: var(--wdc-accent); text-decoration: none; }
.help-prose :deep(a:hover) { text-decoration: underline; }
.help-prose :deep(ul) { padding-left: 20px; margin: 6px 0 12px; }
.help-prose :deep(li) { margin-bottom: 5px; }
.help-prose :deep(strong) { color: var(--wdc-text); font-weight: 600; }
.help-prose :deep(em) { color: var(--wdc-text-2); }

/* Welcome state */
.help-welcome {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 48px 24px;
  text-align: center;
  gap: 12px;
}

.welcome-icon {
  width: 56px;
  height: 56px;
  border-radius: 50%;
  background: var(--wdc-surface-2);
  border: 2px solid var(--wdc-border);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 1.6rem;
  font-weight: 700;
  font-style: italic;
  color: var(--wdc-text-3);
}

.welcome-title {
  font-size: 1.3rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin: 0;
}

.welcome-hint {
  font-size: 0.88rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.welcome-groups {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: center;
  margin-top: 12px;
}

.welcome-group-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 14px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  color: var(--wdc-text-2);
  font-size: 0.84rem;
  cursor: pointer;
  transition: all 0.15s;
}

.welcome-group-btn:hover {
  background: rgba(86, 194, 255, 0.08);
  border-color: var(--wdc-accent);
  color: var(--wdc-accent);
}
</style>
