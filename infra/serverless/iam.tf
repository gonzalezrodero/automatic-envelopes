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
      # Includes /automatic-envelopes/whatsapp/tenants/* for per-tenant access tokens.
      # The worker role sources this document. Do not add aws_ssm_parameter for those values.
      "arn:aws:ssm:${var.aws_region}:${var.aws_account_id}:parameter/automatic-envelopes/*"
    ]
  }

  # Decrypts Secrets Manager payloads and SSM SecureString parameters (default key alias/aws/ssm).
  statement {
    sid    = "AllowKMSDecryptForSecrets"
    effect = "Allow"
    actions = [
      "kms:Decrypt"
    ]
    resources = ["*"]
  }

  # Sonnet stays so reverting bedrock_model_id does not also require an IAM change.
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

  # Geo profiles need the profile ARN and the foundation model in every destination region.
  # Source is eu-west-1. From Ireland, Haiku 4.5 routes to these six regions only.
  statement {
    sid    = "AllowBedrockHaiku45Profile"
    effect = "Allow"
    actions = [
      "bedrock:InvokeModel",
      "bedrock:InvokeModelWithResponseStream"
    ]
    resources = [
      "arn:aws:bedrock:${var.aws_region}:${var.aws_account_id}:inference-profile/eu.anthropic.claude-haiku-4-5-20251001-v1:0"
    ]
  }

  statement {
    sid    = "AllowBedrockHaiku45FoundationModels"
    effect = "Allow"
    actions = [
      "bedrock:InvokeModel",
      "bedrock:InvokeModelWithResponseStream"
    ]
    resources = [
      "arn:aws:bedrock:eu-central-1::foundation-model/anthropic.claude-haiku-4-5-20251001-v1:0",
      "arn:aws:bedrock:eu-north-1::foundation-model/anthropic.claude-haiku-4-5-20251001-v1:0",
      "arn:aws:bedrock:eu-south-1::foundation-model/anthropic.claude-haiku-4-5-20251001-v1:0",
      "arn:aws:bedrock:eu-south-2::foundation-model/anthropic.claude-haiku-4-5-20251001-v1:0",
      "arn:aws:bedrock:eu-west-1::foundation-model/anthropic.claude-haiku-4-5-20251001-v1:0",
      "arn:aws:bedrock:eu-west-3::foundation-model/anthropic.claude-haiku-4-5-20251001-v1:0"
    ]
    condition {
      test     = "StringEquals"
      variable = "bedrock:InferenceProfileArn"
      values = [
        "arn:aws:bedrock:${var.aws_region}:${var.aws_account_id}:inference-profile/eu.anthropic.claude-haiku-4-5-20251001-v1:0"
      ]
    }
  }

}


resource "aws_iam_role_policy" "lambda_custom" {
  name   = "${var.project_name}-lambda-custom-policy"
  role   = aws_iam_role.lambda_exec.id
  policy = data.aws_iam_policy_document.lambda_custom_permissions.json

  lifecycle {
    precondition {
      condition     = var.aws_region == "eu-west-1"
      error_message = "Haiku foundation-model ARNs are the destination list for source eu-west-1. Update that list before changing aws_region."
    }
  }
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