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

function fromProblem(status: number, problem: Problem, headers: Headers): ApiError {
  const retry = Number(headers.get('Retry-After'))
  return new ApiError(
    status,
    problem.code ?? 'UNKNOWN',
    problem.title ?? 'Something went wrong. Please try again.',
    problem.errors ?? {},
    Number.isFinite(retry) && retry > 0 ? retry : null,
  )
}

/** From a raw fetch Response whose body has not been read yet (the auth calls). */
export async function toApiError(response: Response): Promise<ApiError> {
  let problem: Problem = {}
  try {
    problem = (await response.clone().json()) as Problem
  } catch {
    // not JSON — fall through to a generic message
  }

  return fromProblem(response.status, problem, response.headers)
}

/**
 * Unwraps an openapi-fetch result, throwing ApiError on failure. openapi-fetch has ALREADY read the
 * response body and exposes it as `error`, so it must be used here: re-reading the Response would fail
 * and every server message (and field error) would be lost.
 */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.response.ok) return result.data as T
  const problem = result.error && typeof result.error === 'object' ? (result.error as Problem) : {}
  throw fromProblem(result.response.status, problem, result.response.headers)
}
