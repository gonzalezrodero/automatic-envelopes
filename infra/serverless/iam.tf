# ==============================================================================
# 1. TRUST POLICY
# ==============================================================================
data "aws_iam_policy_document" "lambda_assume_role" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "lambda_exec" {
  name               = "${var.project_name}-lambda-exec-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume_role.json
}

# ==============================================================================
# 2. BASIC PERMISSIONS (SQS + CloudWatch Logs)
# ==============================================================================
resource "aws_iam_role_policy_attachment" "lambda_basic_sqs" {
  role       = aws_iam_role.lambda_exec.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaSQSQueueExecutionRole"
}

# ==============================================================================
# 3. CUSTOM PERMISSIONS (Inline Policy)
# ==============================================================================
data "aws_iam_policy_document" "lambda_custom_permissions" {
  statement {
    sid    = "AllowSQSAccess"
    effect = "Allow"
    actions = [
      "sqs:SendMessage",
      "sqs:GetQueueUrl",
      "sqs:GetQueueAttributes"
    ]
    resources = [
      aws_sqs_queue.bot_queue.arn,
      aws_sqs_queue.bot_dlq.arn,
      aws_sqs_queue.system_queue.arn
    ]
  }

  statement {
    sid    = "AllowSecretsAndSSM"
    effect = "Allow"
    actions = [
      "secretsmanager:GetSecretValue",
      "ssm:GetParameters",
      "ssm:GetParameter",
      "ssm:GetParametersByPath"
    ]
    resources = [
      data.terraform_remote_state.database.outputs.db_password_secret_arn,
      "arn:aws:ssm:${var.aws_region}:${var.aws_account_id}:parameter/automatic-envelopes/*"
    ]
  }

  statement {
    sid    = "AllowKMSDecryptForSecrets"
    effect = "Allow"
    actions = [
      "kms:Decrypt"
    ]
    resources = ["*"]
  }

  statement {
    sid    = "AllowBedrockSonnet46"
    effect = "Allow"
    actions = [
      "bedrock:InvokeModel",
      "bedrock:InvokeModelWithResponseStream"
    ]
    resources = [
      "arn:aws:bedrock:${var.aws_region}:${var.aws_account_id}:inference-profile/eu.anthropic.claude-sonnet-4-6",
      "arn:aws:bedrock:*::foundation-model/anthropic.claude-sonnet-4-6",
      "arn:aws:bedrock:*::foundation-model/amazon.titan-embed-text-v2:0"
    ]
  }

}


resource "aws_iam_role_policy" "lambda_custom" {
  name   = "${var.project_name}-lambda-custom-policy"
  role   = aws_iam_role.lambda_exec.id
  policy = data.aws_iam_policy_document.lambda_custom_permissions.json
}

resource "aws_iam_role_policy_attachment" "attach_bootstrap_bedrock_policy" {
  role       = aws_iam_role.lambda_exec.name
  policy_arn = data.terraform_remote_state.bootstrap.outputs.bedrock_policy_arn
}

# The public API lambda keeps lambda_exec. Only the worker sends mail and reads the phone HMAC key.
resource "aws_iam_role" "worker" {
  name               = "${var.project_name}-worker-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume_role.json
}

resource "aws_iam_role_policy_attachment" "worker_basic_sqs" {
  role       = aws_iam_role.worker.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaSQSQueueExecutionRole"
}

resource "aws_iam_role_policy_attachment" "worker_bedrock" {
  role       = aws_iam_role.worker.name
  policy_arn = data.terraform_remote_state.bootstrap.outputs.bedrock_policy_arn
}

data "aws_iam_policy_document" "worker_permissions" {
  source_policy_documents = [data.aws_iam_policy_document.lambda_custom_permissions.json]

  statement {
    sid       = "AllowPhoneHashSecret"
    effect    = "Allow"
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_secretsmanager_secret.phone_hash_key.arn]
  }

  statement {
    sid       = "AllowSesSendFromNoreply"
    effect    = "Allow"
    actions   = ["ses:SendEmail"]
    resources = [aws_sesv2_email_identity.sender.arn]
    condition {
      test     = "StringEquals"
      variable = "ses:FromAddress"
      values   = ["noreply@core-webhook.eu"]
    }
  }
}

resource "aws_iam_role_policy" "worker_custom" {
  name   = "${var.project_name}-worker-custom-policy"
  role   = aws_iam_role.worker.id
  policy = data.aws_iam_policy_document.worker_permissions.json
}