// Errors from the API are RFC 9457 ProblemDetails with a stable `code` (TRD §11.4).
// Titles are written to be user-safe, so the UI shows them directly unless a screen
// needs bespoke wording for a specific code.

export type FieldErrors = Record<string, string[]>

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly fieldErrors: FieldErrors
  readonly retryAfterSeconds: number | null

  constructor(status: number, code: string, message: string, fieldErrors: FieldErrors = {}, retryAfterSeconds: number | null = null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.fieldErrors = fieldErrors
    this.retryAfterSeconds = retryAfterSeconds
  }

  static network(): ApiError {
    return new ApiError(0, 'NETWORK', "Can't reach the server. Check your connection and try again.")
  }
}

type Problem = { title?: string; code?: string; errors?: FieldErrors }

export async function toApiError(response: Response): Promise<ApiError> {
  let problem: Problem = {}
  try {
    problem = (await response.clone().json()) as Problem
  } catch {
    // not JSON — fall through to a generic message
  }

  const retry = Number(response.headers.get('Retry-After'))
  return new ApiError(
    response.status,
    problem.code ?? 'UNKNOWN',
    problem.title ?? 'Something went wrong. Please try again.',
    problem.errors ?? {},
    Number.isFinite(retry) && retry > 0 ? retry : null,
  )
}

/** Unwraps an openapi-fetch result, throwing ApiError on failure. */
export async function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): Promise<T> {
  if (result.response.ok) return result.data as T
  throw await toApiError(result.response)
}
