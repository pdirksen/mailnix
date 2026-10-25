export interface PasswordStrength {
  /** 1 = weak, 2 = okay, 3 = strong */
  level: 1 | 2 | 3
  label: 'weak' | 'fair' | 'strong'
}

/**
 * A rough hint, not a policy: length counts most (as NIST SP 800-63B recommends), variety a little,
 * and repeated or very common patterns pull it down.
 */
export function passwordStrength(password: string): PasswordStrength {
  const classes = [/[a-z]/, /[A-Z]/, /\d/, /[^a-zA-Z\d]/].filter((pattern) => pattern.test(password)).length
  const unique = new Set(password.toLowerCase()).size
  let score = password.length >= 16 ? 2 : password.length >= 12 ? 1 : 0
  if (classes >= 3 || password.includes(' ')) score += 1
  if (unique < 5 || /^(?:password|passwort|qwerty|123456)/i.test(password)) score = 0
  const level = Math.min(3, Math.max(1, score)) as 1 | 2 | 3
  const labels = { 1: 'weak', 2: 'fair', 3: 'strong' } as const
  return { level, label: labels[level] }
}
