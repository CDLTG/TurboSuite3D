using System.Collections.Generic;

namespace TurboSuite.Name.Models;

public record GenerationResult(int Created, int Failed, List<string> FailedDetails);
