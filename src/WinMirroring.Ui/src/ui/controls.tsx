import { ReactNode, useEffect, useState } from 'react'

/** A switch with its label and, under it, the sentence that explains what it does. */
export function Toggle({
  checked,
  onChange,
  label,
  hint,
  disabled,
}: {
  checked: boolean
  onChange: (value: boolean) => void
  label: ReactNode
  hint?: ReactNode
  disabled?: boolean
}) {
  return (
    <div className={`row${disabled ? ' is-disabled' : ''}`}>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        className={`switch${checked ? ' is-on' : ''}`}
        disabled={disabled}
        onClick={() => onChange(!checked)}
      >
        <span className="switch-knob" />
      </button>
      <div className="row-text">
        <span className="row-label" onClick={() => !disabled && onChange(!checked)}>
          {label}
        </span>
        {hint && <p className="hint">{hint}</p>}
      </div>
    </div>
  )
}

/**
 * A number the app only hears about once it is settled: while it is being typed the box keeps whatever is in it,
 * so a half-typed value is never saved.
 */
export function Num({
  value,
  onChange,
  label,
  hint,
  suffix,
  disabled,
}: {
  value: number
  onChange: (value: number) => void
  label: ReactNode
  hint?: ReactNode
  suffix?: string
  disabled?: boolean
}) {
  const [text, setText] = useState(String(value))
  useEffect(() => setText(String(value)), [value])

  const commit = () => {
    const parsed = Number.parseInt(text, 10)
    if (Number.isFinite(parsed) && parsed !== value) onChange(parsed)
    else setText(String(value))
  }

  return (
    <div className={`row is-field${disabled ? ' is-disabled' : ''}`}>
      <div className="row-text">
        <span className="row-label">{label}</span>
        {hint && <p className="hint">{hint}</p>}
      </div>
      <span className="row-input">
        <input
          className="field is-number"
          inputMode="numeric"
          value={text}
          disabled={disabled}
          onChange={(e) => setText(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => e.key === 'Enter' && commit()}
        />
        {suffix && <span className="suffix">{suffix}</span>}
      </span>
    </div>
  )
}

export function Text({
  value,
  onChange,
  label,
  hint,
  placeholder,
}: {
  value: string
  onChange: (value: string) => void
  label: ReactNode
  hint?: ReactNode
  placeholder?: string
}) {
  const [text, setText] = useState(value)
  useEffect(() => setText(value), [value])

  return (
    <div className="row is-field">
      <div className="row-text">
        <span className="row-label">{label}</span>
        {hint && <p className="hint">{hint}</p>}
      </div>
      <span className="row-input">
        <input
          className="field"
          value={text}
          placeholder={placeholder}
          onChange={(e) => setText(e.target.value)}
          onBlur={() => text !== value && onChange(text)}
          onKeyDown={(e) => e.key === 'Enter' && text !== value && onChange(text)}
        />
      </span>
    </div>
  )
}

export function Choice({
  value,
  options,
  onChange,
  label,
  hint,
  disabled,
}: {
  value: string
  options: { value: string; label: string }[]
  onChange: (value: string) => void
  label: ReactNode
  hint?: ReactNode
  disabled?: boolean
}) {
  return (
    <div className={`row is-field${disabled ? ' is-disabled' : ''}`}>
      <div className="row-text">
        <span className="row-label">{label}</span>
        {hint && <p className="hint">{hint}</p>}
      </div>
      <span className="row-input">
        <select className="field is-select" value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </span>
    </div>
  )
}

export function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="section">
      <h2>{title}</h2>
      {children}
    </section>
  )
}
