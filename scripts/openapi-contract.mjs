const acceptedRoutes = new Map([
  ['POST /api/invoices', ['201', '400', '413', '500']],
  ['GET /api/invoices', ['200', '400', '500']],
  ['GET /api/invoices/{id}', ['200', '400', '404', '500']],
  ['GET /api/invoices/{id}/document', ['200', '206', '400', '404', '416', '500']],
  ['PUT /api/invoices/{id}/draft', ['200', '400', '404', '409', '500']],
  ['POST /api/invoices/{id}/validate', ['200', '400', '404', '409', '500']],
  ['POST /api/invoices/{id}/approve', ['200', '400', '404', '409', '500']],
  ['POST /api/invoices/{id}/reject', ['200', '400', '404', '409', '500']],
  ['GET /api/invoices/{id}/history', ['200', '400', '404', '500']],
  ['GET /api/invoices/{id}/export', ['200', '400', '404', '409', '500']],
]);

const operationIds = new Map([
  ['POST /api/invoices', 'uploadInvoice'],
  ['GET /api/invoices', 'listInvoices'],
  ['GET /api/invoices/{id}', 'getInvoice'],
  ['GET /api/invoices/{id}/document', 'getInvoiceDocument'],
  ['PUT /api/invoices/{id}/draft', 'saveInvoiceDraft'],
  ['POST /api/invoices/{id}/validate', 'validateInvoice'],
  ['POST /api/invoices/{id}/approve', 'approveInvoice'],
  ['POST /api/invoices/{id}/reject', 'rejectInvoice'],
  ['GET /api/invoices/{id}/history', 'getInvoiceHistory'],
  ['GET /api/invoices/{id}/export', 'exportInvoice'],
]);

const enumCatalog = {
  InvoiceStatus: ['processing', 'reviewRequired', 'readyForApproval', 'approved', 'rejected', 'processingFailed'],
  InvoiceFieldKey: ['supplierName', 'supplierRegistrationId', 'invoiceNumber', 'purchaseOrderNumber', 'invoiceDate', 'dueDate', 'paymentTerms', 'currency', 'subtotal', 'taxAmount', 'total', 'reviewNotes'],
  FieldSource: ['nativeText', 'ocr', 'aiInference', 'reviewer'],
  DocumentTextSource: ['nativeText', 'ocr'],
  ConfidenceBand: ['high', 'medium', 'low', 'unknown'],
  DecisionKind: ['approved', 'rejected'],
  DocumentIntegrityStatus: ['available', 'missing', 'corrupt'],
  ValidationSeverity: ['warning', 'error'],
  ValidationTrigger: ['initial', 'explicit', 'approval'],
  ProcessingStage: ['upload', 'pdfExtraction', 'ocr', 'aiExtraction', 'parsing', 'persistence', 'startupRecovery'],
  AuditActor: ['system', 'reviewer'],
  InvoiceSort: ['updatedAtDesc', 'updatedAtAsc', 'createdAtDesc', 'createdAtAsc'],
  AuditEventType: ['invoiceUploaded', 'extractionCompleted', 'extractionFailed', 'draftSaved', 'validationCompleted', 'invoiceApproved', 'invoiceRejected', 'documentIntegrityChanged'],
  ValidationCode: ['REQUIRED_FIELD_MISSING', 'AMOUNT_RECONCILIATION_FAILED', 'NEGATIVE_AMOUNT_UNEXPECTED', 'DUE_DATE_BEFORE_INVOICE_DATE', 'PAYMENT_TERMS_MISMATCH', 'POSSIBLE_DUPLICATE_INVOICE', 'CURRENCY_INVALID', 'LOW_EXTRACTION_CONFIDENCE', 'INVOICE_DATE_IN_FUTURE'],
  ProcessingFailureCode: ['PDF_EXTRACTION_FAILED', 'PDF_RENDER_FAILED', 'OCR_UNAVAILABLE', 'OCR_PAGE_TIMEOUT', 'OCR_DOCUMENT_TIMEOUT', 'OCR_FAILED', 'AI_TIMEOUT', 'AI_UNAVAILABLE', 'AI_REFUSED', 'AI_RESPONSE_INCOMPLETE', 'AI_RESPONSE_INVALID', 'PROCESS_INTERRUPTED', 'PROCESSING_FAILED'],
};

const validationVariants = [
  ['REQUIRED_FIELD_MISSING', 'RequiredFieldMissingValidationResultDto', 'RequiredFieldMissingValidationDataDto', 'error'],
  ['AMOUNT_RECONCILIATION_FAILED', 'AmountReconciliationFailedValidationResultDto', 'AmountReconciliationFailedValidationDataDto', 'error'],
  ['NEGATIVE_AMOUNT_UNEXPECTED', 'NegativeAmountUnexpectedValidationResultDto', 'NegativeAmountUnexpectedValidationDataDto', 'warning'],
  ['DUE_DATE_BEFORE_INVOICE_DATE', 'DueDateBeforeInvoiceDateValidationResultDto', 'DueDateBeforeInvoiceDateValidationDataDto', 'error'],
  ['PAYMENT_TERMS_MISMATCH', 'PaymentTermsMismatchValidationResultDto', 'PaymentTermsMismatchValidationDataDto', 'warning'],
  ['POSSIBLE_DUPLICATE_INVOICE', 'PossibleDuplicateInvoiceValidationResultDto', 'PossibleDuplicateInvoiceValidationDataDto', 'error'],
  ['CURRENCY_INVALID', 'CurrencyInvalidValidationResultDto', 'CurrencyInvalidValidationDataDto', 'error'],
  ['LOW_EXTRACTION_CONFIDENCE', 'LowExtractionConfidenceValidationResultDto', 'LowExtractionConfidenceValidationDataDto', 'warning'],
  ['INVOICE_DATE_IN_FUTURE', 'InvoiceDateInFutureValidationResultDto', 'InvoiceDateInFutureValidationDataDto', 'warning'],
];

const auditVariants = [
  ['invoiceUploaded', 'InvoiceUploadedAuditEventDto', 'InvoiceUploadedAuditDetailsDto'],
  ['extractionCompleted', 'ExtractionCompletedAuditEventDto', 'ExtractionCompletedAuditDetailsDto'],
  ['extractionFailed', 'ExtractionFailedAuditEventDto', 'ExtractionFailedAuditDetailsDto'],
  ['draftSaved', 'DraftSavedAuditEventDto', 'DraftSavedAuditDetailsDto'],
  ['validationCompleted', 'ValidationCompletedAuditEventDto', 'ValidationCompletedAuditDetailsDto'],
  ['invoiceApproved', 'InvoiceApprovedAuditEventDto', 'InvoiceApprovedAuditDetailsDto'],
  ['invoiceRejected', 'InvoiceRejectedAuditEventDto', 'InvoiceRejectedAuditDetailsDto'],
  ['documentIntegrityChanged', 'DocumentIntegrityChangedAuditEventDto', 'DocumentIntegrityChangedAuditDetailsDto'],
];

const ref = name => ({ $ref: `#/components/schemas/${name}` });
const array = items => ({ type: 'array', items });
const nullable = schema => ({ oneOf: [{ type: 'null' }, schema] });
const object = properties => ({
  type: 'object',
  properties,
  required: Object.keys(properties),
  additionalProperties: false,
});
const literal = value => ({ type: 'string', enum: [value] });
const integer = (minimum = 0, format = 'int32') => ({ type: 'integer', format, minimum });
const sequenceId = () => ({ type: 'string', pattern: '^(?:0|[1-9]\\d*)$' });
const uuid = () => ({
  type: 'string',
  format: 'uuid',
  pattern: '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
});
const date = () => ({ type: 'string', format: 'date', pattern: '^\\d{4}-\\d{2}-\\d{2}$' });
const timestamp = () => ({
  type: 'string',
  format: 'date-time',
  pattern: '^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$',
});
const money = () => ({
  type: 'string',
  pattern: '^-?(?:0|[1-9]\\d{0,26})\\.\\d{2}$',
});
const confidence = nullableValue => nullableValue
  ? { type: ['null', 'number'], format: 'double', minimum: 0, maximum: 1 }
  : { type: 'number', format: 'double', minimum: 0, maximum: 1 };

function replaceResponse(operation, status, mediaType, schema) {
  const response = operation.responses[status] ?? { description: status };
  response.content = { [mediaType]: { schema } };
  operation.responses[status] = response;
}

function normalizeOperation(operation, method, path) {
  const key = `${method} ${path}`;
  operation.operationId = operationIds.get(key);
  operation.parameters ??= [];

  for (const parameter of operation.parameters) {
    parameter.name = parameter.name[0].toLowerCase() + parameter.name.slice(1);
    if (parameter.in === 'path' && parameter.name === 'id') {
      parameter.required = true;
      parameter.schema = uuid();
    }
    if (parameter.name === 'page') parameter.schema = { ...integer(1), default: 1 };
    if (parameter.name === 'pageSize') {
      parameter.schema = { ...integer(1), maximum: 100, default: path.endsWith('/history') ? 50 : 25 };
    }
    if (parameter.name === 'search') parameter.schema = { type: 'string' };
    if (parameter.name === 'status') {
      parameter.schema = array(ref('InvoiceStatus'));
      parameter.style = 'form';
      parameter.explode = true;
    }
    if (parameter.name === 'sort') parameter.schema = { ...ref('InvoiceSort'), default: 'updatedAtDesc' };
  }

  if (['POST', 'PUT'].includes(method) && key !== 'POST /api/invoices') {
    const json = operation.requestBody?.content?.['application/json'];
    operation.requestBody = { required: true, content: { 'application/json': json } };
  }

  for (const status of Object.keys(operation.responses)) {
    if (['400', '404', '409', '413', '500'].includes(status)) {
      replaceResponse(operation, status, 'application/problem+json', ref('InvoiceProblemDetails'));
      operation.responses[status].headers = {
        'X-Correlation-ID': { schema: { type: 'string' } },
      };
    }
  }

  if (key === 'POST /api/invoices') {
    operation.requestBody = {
      required: true,
      content: {
        'multipart/form-data': {
          schema: object({ file: { type: 'string', format: 'binary' } }),
        },
      },
    };
    replaceResponse(operation, '201', 'application/json', ref('InvoiceDetailDto'));
    operation.responses['201'].headers = { Location: { schema: { type: 'string' } } };
  } else if (key === 'GET /api/invoices') {
    replaceResponse(operation, '200', 'application/json', ref('InvoiceQueuePageDto'));
  } else if (key === 'GET /api/invoices/{id}/document') {
    replaceResponse(operation, '200', 'application/pdf', { type: 'string', format: 'binary' });
    replaceResponse(operation, '206', 'application/pdf', { type: 'string', format: 'binary' });
    delete operation.responses['416'].content;
  } else if (key === 'GET /api/invoices/{id}/history') {
    replaceResponse(operation, '200', 'application/json', ref('AuditHistoryPageDto'));
  } else if (key === 'GET /api/invoices/{id}/export') {
    replaceResponse(operation, '200', 'application/json', ref('InvoiceExportV1Dto'));
    operation.responses['200'].headers = { 'Content-Disposition': { schema: { type: 'string' } } };
  } else {
    replaceResponse(operation, '200', 'application/json', ref('InvoiceDetailDto'));
  }
}

function normalizePrimitiveSchemas(schemas) {
  for (const [name, values] of Object.entries(enumCatalog)) {
    schemas[name] = { type: 'string', enum: values };
  }

  const visit = value => {
    if (!value || typeof value !== 'object') return;
    if (Array.isArray(value.type) && value.type.includes('integer')) {
      value.type = value.type.includes('null') ? ['null', 'integer'] : 'integer';
      delete value.pattern;
    }
    if (value.type === 'object' && value.properties) value.additionalProperties ??= false;
    for (const child of Object.values(value)) visit(child);
  };
  for (const schema of Object.values(schemas)) visit(schema);

  const property = (schemaName, propertyName, value) => {
    schemas[schemaName].properties[propertyName] = value;
  };
  const nullableMoney = () => ({ type: ['null', 'string'], pattern: money().pattern });
  for (const name of ['AmountDraftDto']) {
    for (const key of ['subtotal', 'taxAmount', 'total']) property(name, key, nullableMoney());
  }
  for (const key of ['value', 'originalValue']) property('MoneyFieldDto', key, nullableMoney());
  property('InvoiceQueueItemDto', 'total', nullableMoney());

  for (const name of ['TextFieldDto', 'DateFieldDto', 'MoneyFieldDto']) {
    property(name, 'confidence', confidence(true));
  }

  const minimumOne = [
    ['SaveInvoiceDraftRequest', 'expectedVersion'], ['ValidateInvoiceRequest', 'expectedVersion'],
    ['ApproveInvoiceRequest', 'expectedVersion'], ['RejectInvoiceRequest', 'expectedVersion'],
    ['InvoiceDetailDto', 'draftVersion'], ['InvoiceDetailDto', 'lastValidatedVersion'],
    ['InvoiceQueueItemDto', 'draftVersion'], ['FieldCorrectionDto', 'draftVersion'],
    ['ValidationRunDto', 'draftVersion'],
  ];
  for (const [schemaName, propertyName] of minimumOne) {
    const current = schemas[schemaName].properties[propertyName];
    current.minimum = 1;
  }

  const countProperties = {
    InvoiceSummaryDto: ['extractedFieldCount', 'warningCount', 'errorCount', 'manualCorrectionCount'],
    InvoiceQueueItemDto: ['warningCount', 'errorCount', 'exceptionCount'],
    InvoiceQueueSummaryDto: ['totalInvoiceCount', 'processingCount', 'reviewRequiredCount', 'readyForApprovalCount', 'approvedCount', 'rejectedCount', 'processingFailedCount', 'pendingReviewCount', 'warningInvoiceCount', 'errorInvoiceCount'],
    InvoiceQueuePageDto: ['page', 'pageSize', 'totalItems', 'totalPages'],
    AuditHistoryPageDto: ['page', 'pageSize', 'totalItems', 'totalPages'],
    ValidationRunDto: ['warningCount', 'errorCount'],
  };
  for (const [schemaName, propertyNames] of Object.entries(countProperties)) {
    for (const propertyName of propertyNames) schemas[schemaName].properties[propertyName].minimum = propertyName === 'page' || propertyName === 'pageSize' ? 1 : 0;
  }

  property('InvoiceDetailDto', 'id', uuid());
  property('ValidationRunDto', 'id', uuid());
  schemas.DuplicateInvoiceMatchDto = object({ invoiceId: uuid(), status: ref('InvoiceStatus') });
  property('InvoiceDocumentDto', 'byteLength', integer(1, 'int64'));
  property('InvoiceDocumentDto', 'pageCount', integer(1));
  property('InvoiceDocumentDto', 'mediaType', literal('application/pdf'));
  property('InvoiceDocumentDto', 'sha256', { type: 'string', pattern: '^[0-9a-f]{64}$' });
  property('InvoiceExportV1Dto', 'schemaVersion', literal('1.0'));
  property('FieldCorrectionDto', 'id', sequenceId());
  property('FieldCorrectionDto', 'auditEventId', sequenceId());

  const timestampProperties = {
    TextFieldDto: ['lastCorrectedAt'], DateFieldDto: ['lastCorrectedAt'], MoneyFieldDto: ['lastCorrectedAt'],
    ProcessingFailureDto: ['failedAt'], InvoiceDecisionDto: ['decidedAt'], FieldCorrectionDto: ['occurredAt'],
    ValidationRunDto: ['validatedAt'], InvoiceDetailDto: ['createdAt', 'updatedAt'],
    InvoiceQueueItemDto: ['createdAt', 'updatedAt'],
  };
  for (const [schemaName, propertyNames] of Object.entries(timestampProperties)) {
    for (const propertyName of propertyNames) {
      const current = schemas[schemaName].properties[propertyName];
      schemas[schemaName].properties[propertyName] = current.oneOf || (Array.isArray(current.type) && current.type.includes('null'))
        ? nullable(timestamp())
        : timestamp();
    }
  }
}

function addValidationSchemas(schemas) {
  schemas.RequiredFieldMissingValidationDataDto = object({ missingField: ref('InvoiceFieldKey') });
  schemas.AmountReconciliationFailedValidationDataDto = object({
    currency: { type: 'string' }, subtotal: money(), taxAmount: money(), expectedTotal: money(),
    actualTotal: money(), difference: money(), tolerance: money(),
  });
  schemas.NegativeAmountUnexpectedValidationDataDto = object({ field: ref('InvoiceFieldKey'), amount: money() });
  schemas.DueDateBeforeInvoiceDateValidationDataDto = object({ invoiceDate: date(), dueDate: date() });
  schemas.PaymentTermsMismatchValidationDataDto = object({
    invoiceDate: date(), dueDate: date(), normalizedPaymentTermsDays: integer(0), calculatedDueDate: date(),
  });
  schemas.PossibleDuplicateInvoiceValidationDataDto = object({ matches: array(ref('DuplicateInvoiceMatchDto')) });
  schemas.CurrencyInvalidValidationDataDto = object({
    value: { type: ['null', 'string'] }, allowedCurrencies: array({ type: 'string' }),
  });
  schemas.LowExtractionConfidenceValidationDataDto = object({
    field: ref('InvoiceFieldKey'), confidence: confidence(true), confidenceBand: { type: 'string', enum: ['low', 'unknown'] },
  });
  schemas.InvoiceDateInFutureValidationDataDto = object({ invoiceDate: date(), currentLocalDate: date() });

  const mapping = {};
  for (const [code, resultName, dataName, severity] of validationVariants) {
    schemas[resultName] = {
      allOf: [
        ref('ValidationResultDto'),
        { type: 'object', properties: { data: ref(dataName) }, required: ['data'] },
      ],
    };
    mapping[code] = `#/components/schemas/${resultName}`;
  }
  schemas.ValidationResultDto = object({
    code: ref('ValidationCode'),
    severity: ref('ValidationSeverity'),
    message: { type: 'string' },
    fields: array(ref('InvoiceFieldKey')),
    data: {},
  });
  schemas.ValidationResultDto.discriminator = { propertyName: 'code', mapping };
}

function addAuditSchemas(schemas) {
  schemas.InvoiceUploadedAuditDetailsDto = object({ document: ref('InvoiceDocumentDto') });
  schemas.ExtractionCompletedAuditDetailsDto = object({ documentTextSource: ref('DocumentTextSource'), extractedFieldCount: integer(0) });
  schemas.ExtractionFailedAuditDetailsDto = object({ failure: ref('ProcessingFailureDto') });
  schemas.DraftSavedAuditDetailsDto = object({ isNoOp: { type: 'boolean' }, changes: array(ref('FieldCorrectionDto')) });
  schemas.ValidationCompletedAuditDetailsDto = object({
    trigger: ref('ValidationTrigger'), validationRunId: uuid(), warningCount: integer(0), errorCount: integer(0), resultingStatus: ref('InvoiceStatus'),
  });
  schemas.InvoiceApprovedAuditDetailsDto = object({ decidedAt: timestamp() });
  schemas.InvoiceRejectedAuditDetailsDto = object({ decidedAt: timestamp(), rejectionReason: { type: 'string' } });
  schemas.DocumentIntegrityChangedAuditDetailsDto = object({ previousStatus: ref('DocumentIntegrityStatus'), currentStatus: ref('DocumentIntegrityStatus') });

  const mapping = {};
  for (const [type, eventName, detailsName] of auditVariants) {
    schemas[eventName] = {
      allOf: [
        ref('AuditEventDto'),
        { type: 'object', properties: { details: ref(detailsName) }, required: ['details'] },
      ],
    };
    mapping[type] = `#/components/schemas/${eventName}`;
  }
  schemas.AuditEventDto = object({
    id: sequenceId(), invoiceId: uuid(), type: ref('AuditEventType'), actor: ref('AuditActor'),
    occurredAt: timestamp(), draftVersion: integer(1), details: {},
  });
  schemas.AuditEventDto.discriminator = { propertyName: 'type', mapping };
  schemas.AuditHistoryPageDto.properties.items = array(ref('AuditEventDto'));
  schemas.InvoiceExportV1Dto.properties.auditHistory = array(ref('AuditEventDto'));
}

function normalizeProblemDetails(schemas) {
  schemas.InvoiceProblemDetails = object({
    type: { type: 'string', format: 'uri' },
    title: { type: 'string' },
    status: integer(100),
    detail: { type: 'string' },
    instance: { type: 'string' },
    code: { type: 'string' },
    correlationId: { type: 'string' },
    fields: nullable({ type: 'object', additionalProperties: array({ type: 'string' }) }),
    currentVersion: nullable(integer(1)),
  });
  delete schemas.ProblemDetails;
}

function validate(document) {
  const actualRoutes = [];
  for (const [path, pathItem] of Object.entries(document.paths)) {
    for (const method of Object.keys(pathItem)) actualRoutes.push(`${method.toUpperCase()} ${path}`);
  }
  assertEqual(actualRoutes.sort(), [...acceptedRoutes.keys()].sort(), 'HTTP route set');

  for (const [key, expectedStatuses] of acceptedRoutes) {
    const separator = key.indexOf(' ');
    const method = key.slice(0, separator).toLowerCase();
    const path = key.slice(separator + 1);
    const operation = document.paths[path][method];
    assertEqual(Object.keys(operation.responses).sort(), [...expectedStatuses].sort(), `${key} response codes`);
    assert(operation.operationId === operationIds.get(key), `${key} operationId`);
  }

  const upload = document.paths['/api/invoices'].post.requestBody.content['multipart/form-data'].schema;
  assert(upload.required.length === 1 && upload.required[0] === 'file', 'multipart file part is required and named file');
  assert(upload.properties.file.format === 'binary', 'multipart file part is binary');

  for (const [name, values] of Object.entries(enumCatalog)) {
    const schema = document.components.schemas[name];
    assert(schema?.type === 'string', `${name} is a string enum`);
    assertEqual(schema.enum, values, `${name} values`);
  }

  const schemas = document.components.schemas;
  assert(schemas.ValidationResultDto.discriminator.propertyName === 'code', 'validation code discriminator');
  assert(Object.keys(schemas.ValidationResultDto.discriminator.mapping).length === 9, 'all validation result variants');
  assert(schemas.AuditEventDto.discriminator.propertyName === 'type', 'audit type discriminator');
  assert(Object.keys(schemas.AuditEventDto.discriminator.mapping).length === 8, 'all audit event variants');
  assert(schemas.InvoiceProblemDetails.required.length === 9, 'all Problem Details properties are required');
  assert(schemas.InvoiceQueuePageDto.properties.items.items.$ref.endsWith('/InvoiceQueueItemDto'), 'queue pagination item type');
  assert(schemas.AuditHistoryPageDto.properties.items.items.$ref.endsWith('/AuditEventDto'), 'history pagination item type');
  assert(schemas.InvoiceExportV1Dto.properties.auditHistory.items.$ref.endsWith('/AuditEventDto'), 'export audit item type');
  assert(schemas.FieldCorrectionDto.properties.id.type === 'string', 'correction sequence ID is a string');
  assert(schemas.InvoiceDetailDto.properties.id.format === 'uuid', 'invoice ID is a UUID');
  assert(schemas.DateFieldDto.properties.value.format === 'date', 'date fields use date format');
  assert(schemas.MoneyFieldDto.properties.value.type.includes('string'), 'money fields use strings');

  for (const schema of Object.values(schemas)) {
    if (!schema?.properties || !schema.required) continue;
    assertEqual([...schema.required].sort(), Object.keys(schema.properties).sort(), 'declared response/request properties are required');
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(`OpenAPI contract validation failed: ${message}.`);
}

function assertEqual(actual, expected, message) {
  assert(JSON.stringify(actual) === JSON.stringify(expected), `${message}; expected ${JSON.stringify(expected)}, received ${JSON.stringify(actual)}`);
}

export function normalizeAndValidateContract(document) {
  document.paths = Object.fromEntries(Object.entries(document.paths).filter(([path]) => path.startsWith('/api/invoices')));
  for (const [path, pathItem] of Object.entries(document.paths)) {
    for (const [method, operation] of Object.entries(pathItem)) normalizeOperation(operation, method.toUpperCase(), path);
  }

  const schemas = document.components?.schemas;
  if (!schemas) throw new Error('OpenAPI contract validation failed: components.schemas is missing.');
  normalizePrimitiveSchemas(schemas);
  addValidationSchemas(schemas);
  addAuditSchemas(schemas);
  normalizeProblemDetails(schemas);
  document.tags = [...new Set(Object.values(document.paths).flatMap(pathItem => Object.values(pathItem).flatMap(operation => operation.tags ?? [])))].map(name => ({ name }));
  validate(document);
  return document;
}

export const acceptedRouteKeys = [...acceptedRoutes.keys()];
