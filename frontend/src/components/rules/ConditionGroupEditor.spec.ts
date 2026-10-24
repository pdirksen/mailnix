import { i18n } from '@/i18n'
import { provideRuleEditor } from '@/rules/editorContext'
import { type ConditionGroup, type FieldDefinition, newCondition, newGroup } from '@/rules/model'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import ConditionGroupEditor from './ConditionGroupEditor.vue'

const fields: FieldDefinition[] = [
  { field: 'From', category: 'Sender', valueType: 'Address', operators: ['Equals', 'Contains', 'InList'], requiresHeaderName: false },
  { field: 'IsNewsletter', category: 'MailType', valueType: 'Boolean', operators: ['IsTrue', 'IsFalse'], requiresHeaderName: false },
]

function mountEditor(initial: ConditionGroup, errors: Record<string, string> = {}) {
  const model = ref(initial)
  const Harness = defineComponent({
    setup() {
      provideRuleEditor({
        catalog: ref(new Map(fields.map((f) => [f.field, f]))),
        lists: ref([]),
        rules: ref([]),
        errors: ref(errors),
        folders: ref([]),
        delimiter: ref('/'),
      })
      return () =>
        h(ConditionGroupEditor, {
          modelValue: model.value,
          'onUpdate:modelValue': (v: ConditionGroup) => (model.value = v),
          path: 'conditions',
        })
    },
  })
  const wrapper = mount(Harness, { global: { plugins: [i18n] }, attachTo: document.body })
  return { wrapper, model }
}

const fromCondition = { kind: 'condition', field: 'From', operator: 'Equals', value: null, headerName: null, caseSensitive: false }

describe('ConditionGroupEditor', () => {
  it('adds conditions and nested groups in the API shape', async () => {
    const { wrapper, model } = mountEditor(newGroup('All', []))

    await wrapper.find('[data-testid="add-condition"]').trigger('click')
    await wrapper.find('[data-testid="add-group"]').trigger('click')

    expect(JSON.parse(JSON.stringify(model.value))).toEqual({
      kind: 'group',
      operator: 'All',
      children: [fromCondition, { kind: 'group', operator: 'Any', children: [fromCondition] }],
    })
    expect(wrapper.findAll('[data-testid="condition-group"]')).toHaveLength(1)
  })

  it('removes conditions and groups', async () => {
    const condition = newCondition(fields[1]!)
    const { wrapper, model } = mountEditor(newGroup('All', [condition, newGroup('Any', [newCondition(fields[0]!)])]))

    await wrapper.find('[data-testid="condition-group"]').find('button.ml-auto').trigger('click')
    expect(model.value.children).toEqual([condition])

    await wrapper.find('[data-testid="condition-row"]').findAll('button').at(-1)!.trigger('click')
    expect(model.value.children).toEqual([])
  })

  it('edits the value of a condition', async () => {
    const { wrapper, model } = mountEditor(newGroup('All', [newCondition(fields[0]!)]))

    await wrapper.find('input').setValue('news@shop.example')

    expect(model.value.children[0]).toMatchObject({ field: 'From', value: 'news@shop.example' })
  })

  it('shows validation errors at the right node', () => {
    const { wrapper } = mountEditor(newGroup('All', [newCondition(fields[0]!)]), { 'conditions.children[0].value': 'Enter a value.' })

    expect(wrapper.find('[data-testid="condition-row"]').text()).toContain('Enter a value.')
  })
})
