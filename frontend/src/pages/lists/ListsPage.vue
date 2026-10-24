<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { Skeleton } from '@/components/ui/skeleton'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { LIST_KINDS, type ListKind, useListsStore } from '@/stores/lists'
import { ListPlusIcon, Loader2Icon, PlusIcon } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'

const store = useListsStore()
const router = useRouter()
const { t } = useI18n()
const { describeRequestError } = useConnectionErrors()
const loading = ref(!store.loaded)

onMounted(async () => {
  try {
    await store.fetchAll()
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    loading.value = false
  }
})

const dialogOpen = ref(false)
const name = ref('')
const kind = ref<ListKind>('Address')
const nameError = ref<string>()
const creating = ref(false)

async function create() {
  nameError.value = name.value.trim() ? undefined : t('validation.required')
  if (nameError.value) return
  creating.value = true
  try {
    const list = await store.create({ name: name.value.trim(), kind: kind.value, entries: [] })
    toast.success(t('lists.created'))
    dialogOpen.value = false
    await router.push({ name: 'list-detail', params: { id: list.id } })
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    creating.value = false
  }
}
</script>

<template>
  <div class="mx-auto grid max-w-5xl gap-6">
    <div class="flex flex-wrap items-end justify-between gap-4">
      <div>
        <h2 class="text-2xl font-semibold tracking-tight">{{ $t('lists.title') }}</h2>
        <p class="text-muted-foreground mt-1 max-w-2xl">{{ $t('lists.subtitle') }}</p>
      </div>
      <Dialog v-model:open="dialogOpen">
        <DialogTrigger as-child>
          <Button>
            <PlusIcon />
            {{ $t('lists.add') }}
          </Button>
        </DialogTrigger>
        <DialogContent>
          <form novalidate class="grid gap-4" @submit.prevent="create">
            <DialogHeader>
              <DialogTitle>{{ $t('lists.add') }}</DialogTitle>
              <DialogDescription>{{ $t('lists.kindFixed') }}</DialogDescription>
            </DialogHeader>
            <FormField id="list-name" v-slot="aria" :label="$t('common.name')" :error="nameError">
              <Input id="list-name" v-model="name" v-bind="aria" autocomplete="off" />
            </FormField>
            <fieldset class="grid gap-2">
              <legend class="mb-2 text-sm font-medium">{{ $t('lists.kind') }}</legend>
              <RadioGroup v-model="kind" class="grid gap-3">
                <div v-for="option in LIST_KINDS" :key="option" class="flex items-start gap-3">
                  <RadioGroupItem :id="`kind-${option}`" :value="option" class="mt-0.5" />
                  <Label :for="`kind-${option}`" class="grid gap-1 font-normal">
                    <span class="font-medium">{{ $t(`lists.kinds.${option}`) }}</span>
                    <span class="text-muted-foreground text-xs">{{ $t(`lists.kinds.${option}Hint`) }}</span>
                  </Label>
                </div>
              </RadioGroup>
            </fieldset>
            <DialogFooter>
              <Button type="submit" :disabled="creating" data-testid="create-list">
                <Loader2Icon v-if="creating" class="animate-spin" />
                {{ $t('common.create') }}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>

    <div v-if="loading && store.lists.length === 0" class="grid gap-4 md:grid-cols-2">
      <Skeleton v-for="i in 2" :key="i" class="h-28 rounded-xl" />
    </div>

    <Card v-else-if="store.lists.length === 0" class="items-center py-12 text-center">
      <CardContent class="grid justify-items-center gap-3">
        <span class="bg-muted flex size-12 items-center justify-center rounded-full">
          <ListPlusIcon class="text-muted-foreground size-6" />
        </span>
        <p class="font-medium">{{ $t('lists.empty') }}</p>
        <p class="text-muted-foreground text-sm">{{ $t('lists.emptyHint') }}</p>
      </CardContent>
    </Card>

    <ul v-else class="grid gap-4 md:grid-cols-2" role="list">
      <li v-for="list in store.lists" :key="list.id">
        <RouterLink
          :to="{ name: 'list-detail', params: { id: list.id } }"
          class="focus-visible:ring-ring/50 block rounded-xl outline-none focus-visible:ring-3"
          :data-testid="`list-card-${list.name}`"
        >
          <Card class="hover:bg-accent/40 h-full transition-colors">
            <CardHeader>
              <div class="flex items-start justify-between gap-2">
                <CardTitle class="truncate">{{ list.name }}</CardTitle>
                <Badge variant="secondary">{{ $t(`lists.kinds.${list.kind}`) }}</Badge>
              </div>
              <CardDescription>
                {{ $t('lists.entries', { count: list.entryCount }, list.entryCount) }} ·
                {{ list.usedByRuleCount > 0 ? $t('lists.usedBy', { count: list.usedByRuleCount }, list.usedByRuleCount) : $t('lists.unused') }}
              </CardDescription>
            </CardHeader>
            <CardContent v-if="list.description" class="text-muted-foreground text-sm">{{ list.description }}</CardContent>
          </Card>
        </RouterLink>
      </li>
    </ul>
  </div>
</template>
