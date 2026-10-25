import { describe, expect, it } from 'vitest'
import { passwordStrength } from './passwordStrength'

describe('passwordStrength', () => {
  it.each([
    ['abcdefghij', 'weak'],
    ['passwort12345678', 'weak'],
    ['aaaaaaaaaaaaaaaaaa', 'weak'],
    ['Tr0ub4dor&3x', 'fair'],
    ['correct horse battery', 'strong'],
    ['Correct-Horse-Battery-9', 'strong'],
  ])('%s is %s', (password, label) => {
    expect(passwordStrength(password).label).toBe(label)
  })
})
