import { i18n } from '@/i18n'
import { apiMock, apiResult } from '@/test/apiMock'
import { testRun } from '@/test/runFixtures'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import ActionsList from './ActionsList.vue'
import KpiTile from '../stats/KpiTile.vue'

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: (await import('@/test/apiMock')).apiMock,
}))

vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const router = createRouter({
  history: createMemoryHistory(),
  routes: [
    { path: '/', component: { render: () => null } },
    { path: '/rules/:id', name: 'rule-edit', component: { render: () => null } },
  ],
})

function entry(id: string, overrides: Record<string, unknown> = {}) {
  return {
    id,
    runId: 'run-1',
    accountId: 'acc-1',
    accountName: 'Work',
    ruleId: 'rule-1',
    ruleName: 'Newsletters',
    action: 'MoveTo',
    keyword: null,
    sourceFolder: 'INBOX',
    targetFolder: 'INBOX.News',
    from: 'news@shop.example',
    subject: `Deals ${id}`,
    dryRun: false,
    success: true,
    error: null,
    createdAt: '2026-10-24T10:00:00Z',
    undoneAt: null,
    canUndo: true,
    ...overrides,
  }
}

describe('ActionsList', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
    i18n.global.locale.value = 'en'
  })

  it('pages through actions and undoes a selection', async () => {
    apiMock.GET.mockResolvedValueOnce(
      apiResult(200, {
        items: [entry('a1'), entry('a2', { canUndo: false, undoneAt: '2026-10-24T11:00:00Z' })],
        nextBefore: 'a2',
      }),
    )
    apiMock.GET.mockResolvedValueOnce(
      apiResult(200, { items: [entry('a3', { action: 'Flag', targetFolder: null })], nextBefore: null }),
    )
    apiMock.POST.mockResolvedValue(apiResult(202, [testRun({ id: 'undo-1', trigger: 'Undo', state: 'Queued' })]))
    const wrapper = mount(ActionsList, {
      props: { filters: { ruleId: 'rule-1' }, selectable: true },
      global: { plugins: [i18n, router] },
      attachTo: document.body,
    })
    await flushPromises()

    expect(apiMock.GET).toHaveBeenCalledWith('/api/activity/actions', {
      params: { query: { ruleId: 'rule-1', limit: 50, before: undefined } },
    })
    expect(wrapper.text()).toContain('Move to folder: INBOX.News')
    expect(wrapper.text()).toContain('Undone')

    await wrapper.get('[data-testid="load-more"]').trigger('click')
    await flushPromises()
    expect(apiMock.GET).toHaveBeenLastCalledWith('/api/activity/actions', {
      params: { query: { ruleId: 'rule-1', limit: 50, before: 'a2' } },
    })
    expect(wrapper.findAll('li')).toHaveLength(3)
    expect(wrapper.find('[data-testid="load-more"]').exists()).toBe(false)

    // Select all that can be undone (a2 cannot), then undo.
    await wrapper.get('button[role="checkbox"]').trigger('click')
    await wrapper.get('[data-testid="undo-selected"]').trigger('click')
    expect(wrapper.get('[data-testid="undo-selected"]').text()).toContain('Undo 2 actions')
    await flushPromises()
    ;(document.querySelector('[data-testid="confirm-undo-selected"]') as HTMLElement).click()
    await flushPromises()

    expect(apiMock.POST).toHaveBeenCalledWith('/api/activity/actions/undo', { body: { actionIds: ['a1', 'a3'] } })
    wrapper.unmount()
  })
})

describe('KpiTile', () => {
  it('colors the change by whether up is good', () => {
    const failures = mount(KpiTile, {
      props: { label: 'Failed', value: 5, previous: 2, upIsGood: false, previousLabel: 'vs. yesterday' },
      global: { plugins: [i18n, router] },
    })
    expect(failures.text()).toContain('+3')
    expect(failures.html()).toContain('text-destructive')

    const neutral = mount(KpiTile, {
      props: { label: 'New mail', value: 12345, previous: 12345 },
      global: { plugins: [i18n, router] },
    })
    expect(neutral.text()).toContain('12.3K')
    expect(neutral.html()).toContain('text-muted-foreground')

    // Neither good nor bad when it is not said which direction is better.
    const unrated = mount(KpiTile, { props: { label: 'New mail', value: 48, previous: 52 }, global: { plugins: [i18n, router] } })
    expect(unrated.text()).toContain('-4')
    expect(unrated.html()).not.toContain('text-destructive')
    expect(unrated.html()).not.toContain('text-success')
  })
})
